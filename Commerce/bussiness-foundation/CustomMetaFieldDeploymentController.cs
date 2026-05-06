using Mediachase.BusinessFoundation.Data;
using Mediachase.BusinessFoundation.Data.Meta.Management;
using Mediachase.BusinessFoundation.Data.Sql;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Foundation.Custom.Episerver_util_api.Commerce.BusinessFoundation
{
    /// <summary>
    /// Reproduces the Commerce metafield deployment issue where Table.AddColumn lacks IF NOT EXISTS guard,
    /// causing SQL 2705 ("Column names in each table must be unique") when a column already exists
    /// but the mcmd_MetaField row is missing (orphan-column state).
    /// 
    /// Root cause: DDL (ALTER TABLE ADD) and metadata-row insert are not transactionally atomic.
    /// If the process fails after ALTER TABLE but before mcmd_MetaField insert, the column persists
    /// without a metadata row. On next startup, the in-memory cache check passes and re-runs ALTER TABLE,
    /// hitting SQL 2705.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-meta-field-deployment")]
    public class CustomMetaFieldDeploymentController : ControllerBase
    {
        private const string TestFieldName = "TestMiscFee";
        private const string TestFieldFriendlyName = "Test Miscellaneous Fee";
        private const string OrganizationClassName = "Organization";
        private const string OrganizationTableName = "cls_Organization";

        /// <summary>
        /// Step 1: Inspect the Organization metaclass — compare fields in mcmd_MetaField vs columns in INFORMATION_SCHEMA.COLUMNS.
        /// This reveals any orphan columns (column exists in SQL but no mcmd_MetaField row) or missing columns.
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/inspect-organization
        /// </summary>
        [HttpGet("inspect-organization")]
        public IActionResult InspectOrganization()
        {
            try
            {
                // Get metafield names from the in-memory metamodel cache
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass == null)
                {
                    return BadRequest("Organization metaclass not found in metamodel cache.");
                }

                var cachedFields = metaClass.Fields.Cast<MetaField>().Select(f => new
                {
                    f.Name,
                    f.FriendlyName,
                    TypeName = f.TypeName,
                    AccessLevel = f.AccessLevel.ToString(),
                    DataSourceTable = f.DataSource?.Table,
                    DataSourceColumns = f.DataSource?.Columns?.Cast<string>().ToList()
                }).ToList();

                // Get actual SQL columns from INFORMATION_SCHEMA.COLUMNS
                var sqlColumns = new List<object>();
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' ORDER BY ORDINAL_POSITION"))
                {
                    while (reader.Read())
                    {
                        sqlColumns.Add(new
                        {
                            ColumnName = reader["COLUMN_NAME"].ToString(),
                            DataType = reader["DATA_TYPE"].ToString(),
                            IsNullable = reader["IS_NULLABLE"].ToString()
                        });
                    }
                }

                // Get mcmd_MetaField rows from DB
                var metaFieldRows = new List<object>();
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $@"SELECT mf.MetaFieldId, mf.Name, mf.FriendlyName 
                       FROM mcmd_MetaField mf 
                       INNER JOIN mcmd_MetaClass mc ON mf.MetaClassId = mc.MetaClassId 
                       WHERE mc.Name = '{OrganizationClassName}' 
                       ORDER BY mf.MetaFieldId"))
                {
                    while (reader.Read())
                    {
                        metaFieldRows.Add(new
                        {
                            MetaFieldId = (int)reader["MetaFieldId"],
                            Name = reader["Name"].ToString(),
                            FriendlyName = reader["FriendlyName"].ToString()
                        });
                    }
                }

                // Identify orphan columns (in SQL but not in mcmd_MetaField)
                var metaFieldNames = cachedFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sqlColumnNames = sqlColumns.Select(c => ((dynamic)c).ColumnName as string).ToList();
                var orphanColumns = sqlColumnNames
                    .Where(col => !metaFieldNames.Contains(col) && col != "OrganizationId" && col != "CardOwnerId")
                    .ToList();

                return Ok(new
                {
                    success = true,
                    message = "Inspection of Organization metaclass completed",
                    cachedFieldCount = cachedFields.Count,
                    sqlColumnCount = sqlColumns.Count,
                    metaFieldRowCount = metaFieldRows.Count,
                    cachedFields,
                    sqlColumns,
                    metaFieldRows,
                    orphanColumns,
                    hasOrphanColumns = orphanColumns.Any(),
                    explanation = "Orphan columns (present in SQL table but missing from mcmd_MetaField) are the root cause of SQL 2705 on next deployment."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Add a test decimal field to Organization via MetaFieldBuilder.CreateDecimal — the standard (non-idempotent) approach.
        /// This demonstrates the normal happy-path that customers use in their migration code (e.g. BFMetaClassExtensions.Create).
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/add-field-standard
        /// </summary>
        [HttpGet("add-field-standard")]
        public IActionResult AddFieldStandard()
        {
            try
            {
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass == null)
                {
                    return BadRequest("Organization metaclass not found.");
                }

                // Check if field already exists in cache (this is the ONLY guard the platform provides)
                if (metaClass.Fields.Contains(TestFieldName))
                {
                    return Ok(new
                    {
                        success = true,
                        alreadyExists = true,
                        message = $"Field '{TestFieldName}' already exists in the metamodel cache. No action taken.",
                        warning = "This guard only checks the in-memory cache, NOT the actual SQL column. If the mcmd_MetaField row is missing but the column exists, this check passes and ALTER TABLE will fail with SQL 2705."
                    });
                }

                using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.Customization))
                {
                    using (var builder = new MetaFieldBuilder(metaClass))
                    {
                        // This calls MetaClass.CreateMetaField -> DefaultMetaFieldInstaller.AssignDataSource -> Table.AddColumn
                        // Table.AddColumn does: ALTER TABLE cls_Organization ADD [TestMiscFee] decimal(18,4) NULL
                        // There is NO IF NOT EXISTS guard — if the column already exists, SQL 2705 is raised.
                        builder.CreateDecimal(TestFieldName, TestFieldFriendlyName, true, 0m);
                        builder.SaveChanges();
                    }
                    scope.SaveChanges();
                }

                // Verify the field was created
                metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                var field = metaClass.Fields[TestFieldName];

                return Ok(new
                {
                    success = true,
                    message = $"Field '{TestFieldName}' created successfully on Organization metaclass via standard (non-idempotent) approach.",
                    fieldDetails = new
                    {
                        field.Name,
                        field.FriendlyName,
                        TypeName = field.TypeName,
                        AccessLevel = field.AccessLevel.ToString(),
                        field.IsNullable,
                        DataSourceTable = field.DataSource?.Table
                    },
                    nextStep = "Run 'simulate-orphan-column' to create the corruption scenario, then 'trigger-sql-2705' to reproduce the error."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Simulate orphan-column corruption by deleting the mcmd_MetaField row but leaving the SQL column.
        /// This replicates what happens when ALTER TABLE succeeds but the subsequent mcmd_MetaField insert fails
        /// (e.g. process killed, concurrent instance, or MetaFieldBuilder reuse bug from World 2018 thread).
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/simulate-orphan-column
        /// </summary>
        [HttpGet("simulate-orphan-column")]
        public IActionResult SimulateOrphanColumn()
        {
            try
            {
                // Step A: Verify the field exists in both places
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass == null || !metaClass.Fields.Contains(TestFieldName))
                {
                    return BadRequest($"Field '{TestFieldName}' not found. Please run 'add-field-standard' first.");
                }

                // Check that the SQL column exists
                var columnExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' AND COLUMN_NAME = '{TestFieldName}'"))
                {
                    columnExists = reader.Read();
                }

                if (!columnExists)
                {
                    return BadRequest($"SQL column '{TestFieldName}' not found in {OrganizationTableName}. Something is wrong.");
                }

                // Step B: Delete ONLY the mcmd_MetaField row (leave the SQL column intact)
                // This simulates the corruption: column exists in SQL, but no metadata row.
                var metaClassId = -1;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT MetaClassId FROM mcmd_MetaClass WHERE Name = '{OrganizationClassName}'"))
                {
                    if (reader.Read()) metaClassId = (int)reader["MetaClassId"];
                }

                var rowsDeleted = SqlHelper.ExecuteNonQuery(
                    SqlContext.Current,
                    CommandType.Text,
                    $"DELETE FROM mcmd_MetaField WHERE Name = '{TestFieldName}' AND MetaClassId = {metaClassId}");

                // Step C: Force the in-memory metamodel cache to refresh
                // We do a no-op edit/save cycle which triggers the SqlSerializer to clear and re-read from DB.
                using (var refreshScope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.System))
                {
                    refreshScope.SaveChanges();
                }

                // Verify the orphan state
                metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                var fieldInCache = metaClass.Fields.Contains(TestFieldName);

                var columnStillExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' AND COLUMN_NAME = '{TestFieldName}'"))
                {
                    columnStillExists = reader.Read();
                }

                return Ok(new
                {
                    success = true,
                    message = "Orphan-column corruption simulated successfully.",
                    state = new
                    {
                        fieldInMetamodelCache = fieldInCache,
                        columnExistsInSql = columnStillExists,
                        mcmdMetaFieldRowsDeleted = rowsDeleted,
                        isOrphanState = !fieldInCache && columnStillExists
                    },
                    explanation = "The SQL column exists but the mcmd_MetaField row is gone. On the next cold start, " +
                                  "the in-memory check (Fields.Contains) passes, and MetaFieldBuilder.CreateDecimal " +
                                  "calls ALTER TABLE ADD — which hits SQL 2705.",
                    nextStep = "Run 'trigger-sql-2705' to see the actual error."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Attempt to re-add the same field — triggers SQL 2705 because the column already exists (orphan).
        /// This is the exact error from the customer's stack trace: Table.AddColumn -> ALTER TABLE ADD -> SQL 2705.
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/trigger-sql-2705
        /// </summary>
        [HttpGet("trigger-sql-2705")]
        public IActionResult TriggerSql2705()
        {
            try
            {
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass == null)
                {
                    return BadRequest("Organization metaclass not found.");
                }

                // The in-memory cache check — this PASSES because we deleted the mcmd_MetaField row
                var fieldInCache = metaClass.Fields.Contains(TestFieldName);

                // Check if column actually exists in SQL
                var columnExistsInSql = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' AND COLUMN_NAME = '{TestFieldName}'"))
                {
                    columnExistsInSql = reader.Read();
                }

                if (fieldInCache)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Field still exists in cache — orphan state not established. Run 'simulate-orphan-column' first.",
                        fieldInCache,
                        columnExistsInSql
                    });
                }

                if (!columnExistsInSql)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "SQL column doesn't exist — cannot reproduce SQL 2705. Run 'add-field-standard' then 'simulate-orphan-column' first.",
                        fieldInCache,
                        columnExistsInSql
                    });
                }

                // NOW: attempt to create the field again — this WILL fail with SQL 2705
                try
                {
                    using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.Customization))
                    {
                        using (var builder = new MetaFieldBuilder(metaClass))
                        {
                            builder.CreateDecimal(TestFieldName, TestFieldFriendlyName, true, 0m);
                            builder.SaveChanges();
                        }
                        scope.SaveChanges();
                    }

                    return Ok(new
                    {
                        success = false,
                        message = "Unexpectedly succeeded — SQL 2705 was not triggered. The orphan state may not have been established correctly."
                    });
                }
                catch (Exception innerEx)
                {
                    // Force metamodel cache refresh to clean up partial state
                    using (var refreshScope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.System))
                    {
                        refreshScope.SaveChanges();
                    }

                    return Ok(new
                    {
                        success = true,
                        message = "SQL 2705 reproduced successfully!",
                        errorType = innerEx.GetType().FullName,
                        errorMessage = innerEx.Message,
                        innerErrorMessage = innerEx.InnerException?.Message,
                        rootCause = "Table.AddColumn emits ALTER TABLE ADD without IF NOT EXISTS. " +
                                    "The column already exists (orphan) so SQL Server returns error 2705.",
                        stackTraceSnippet = innerEx.StackTrace?.Split('\n').Take(5).ToArray(),
                        matchesCustomerTrace = innerEx.StackTrace?.Contains("AddColumn") == true,
                        nextStep = "Run 'add-field-safe' to see the idempotent approach, or 'cleanup' to remove the test column."
                    });
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Demonstrate the safe/idempotent approach — guard both INFORMATION_SCHEMA.COLUMNS and mcmd_MetaField before creating.
        /// This is the recommended pattern for partner code running in multi-instance DXP environments.
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/add-field-safe
        /// </summary>
        [HttpGet("add-field-safe")]
        public IActionResult AddFieldSafe()
        {
            try
            {
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass == null)
                {
                    return BadRequest("Organization metaclass not found.");
                }

                var diagnostics = new List<object>();

                // Guard 1: Check in-memory metamodel cache
                var fieldInCache = metaClass.Fields.Contains(TestFieldName);
                diagnostics.Add(new { check = "MetaModel cache", fieldExists = fieldInCache });

                // Guard 2: Check mcmd_MetaField table directly (bypasses cache)
                var metaFieldRowExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $@"SELECT 1 FROM mcmd_MetaField mf 
                       INNER JOIN mcmd_MetaClass mc ON mf.MetaClassId = mc.MetaClassId 
                       WHERE mc.Name = '{OrganizationClassName}' AND mf.Name = '{TestFieldName}'"))
                {
                    metaFieldRowExists = reader.Read();
                }
                diagnostics.Add(new { check = "mcmd_MetaField row", exists = metaFieldRowExists });

                // Guard 3: Check INFORMATION_SCHEMA.COLUMNS (the actual SQL column)
                var sqlColumnExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' AND COLUMN_NAME = '{TestFieldName}'"))
                {
                    sqlColumnExists = reader.Read();
                }
                diagnostics.Add(new { check = "INFORMATION_SCHEMA.COLUMNS", columnExists = sqlColumnExists });

                // Decision logic
                string action;
                if (fieldInCache && metaFieldRowExists && sqlColumnExists)
                {
                    action = "SKIP — field fully exists (cache + DB row + SQL column). Nothing to do.";
                }
                else if (!fieldInCache && !metaFieldRowExists && !sqlColumnExists)
                {
                    action = "CREATE — field is completely absent. Safe to create.";

                    using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.Customization))
                    {
                        using (var builder = new MetaFieldBuilder(metaClass))
                        {
                            builder.CreateDecimal(TestFieldName, TestFieldFriendlyName, true, 0m);
                            builder.SaveChanges();
                        }
                        scope.SaveChanges();
                    }
                }
                else if (sqlColumnExists && !metaFieldRowExists)
                {
                    action = "REPAIR — orphan column detected. Dropping SQL column first, then re-creating cleanly.";

                    // Drop the orphan column
                    SqlHelper.ExecuteNonQuery(
                        SqlContext.Current,
                        CommandType.Text,
                        $"ALTER TABLE [dbo].[{OrganizationTableName}] DROP COLUMN IF EXISTS [{TestFieldName}]");

                    // Force metamodel cache refresh
                    using (var refreshScope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.System))
                    {
                        refreshScope.SaveChanges();
                    }
                    metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];

                    // Now create cleanly
                    using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.Customization))
                    {
                        using (var builder = new MetaFieldBuilder(metaClass))
                        {
                            builder.CreateDecimal(TestFieldName, TestFieldFriendlyName, true, 0m);
                            builder.SaveChanges();
                        }
                        scope.SaveChanges();
                    }
                }
                else
                {
                    action = $"UNEXPECTED STATE — cache={fieldInCache}, dbRow={metaFieldRowExists}, sqlCol={sqlColumnExists}. Manual investigation needed.";
                }

                diagnostics.Add(new { check = "Action taken", action });

                return Ok(new
                {
                    success = true,
                    message = "Safe/idempotent field creation completed.",
                    diagnostics,
                    recommendation = "Always check BOTH mcmd_MetaField AND INFORMATION_SCHEMA.COLUMNS before calling CreateDecimal/CreateText/etc. " +
                                     "The in-memory cache alone is insufficient in multi-instance environments."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Clean up — remove the test field from both mcmd_MetaField and the SQL column.
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/cleanup
        /// </summary>
        [HttpGet("cleanup")]
        public IActionResult Cleanup()
        {
            try
            {
                var results = new List<object>();

                // Remove via metamodel API if field exists in cache
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                if (metaClass != null && metaClass.Fields.Contains(TestFieldName))
                {
                    try
                    {
                        using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.System))
                        {
                            metaClass.DeleteMetaField(TestFieldName);
                            scope.SaveChanges();
                        }
                        results.Add(new { step = "DeleteMetaField via API", success = true });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { step = "DeleteMetaField via API", success = false, error = ex.Message });
                    }
                }
                else
                {
                    results.Add(new { step = "DeleteMetaField via API", success = true, note = "Field not in cache, skipped." });
                }

                // Clean up any orphan mcmd_MetaField row
                try
                {
                    var metaClassId = -1;
                    using (var reader = SqlHelper.ExecuteReader(
                        SqlContext.Current,
                        CommandType.Text,
                        $"SELECT MetaClassId FROM mcmd_MetaClass WHERE Name = '{OrganizationClassName}'"))
                    {
                        if (reader.Read()) metaClassId = (int)reader["MetaClassId"];
                    }

                    if (metaClassId > 0)
                    {
                        var rows = SqlHelper.ExecuteNonQuery(
                            SqlContext.Current,
                            CommandType.Text,
                            $"DELETE FROM mcmd_MetaField WHERE Name = '{TestFieldName}' AND MetaClassId = {metaClassId}");
                        results.Add(new { step = "Delete orphan mcmd_MetaField row", success = true, rowsDeleted = rows });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { step = "Delete orphan mcmd_MetaField row", success = false, error = ex.Message });
                }

                // Drop the SQL column if it still exists
                try
                {
                    SqlHelper.ExecuteNonQuery(
                        SqlContext.Current,
                        CommandType.Text,
                        $"ALTER TABLE [dbo].[{OrganizationTableName}] DROP COLUMN IF EXISTS [{TestFieldName}]");
                    results.Add(new { step = "Drop SQL column", success = true });
                }
                catch (Exception ex)
                {
                    results.Add(new { step = "Drop SQL column", success = false, error = ex.Message });
                }

                // Force metamodel cache refresh via no-op edit/save cycle
                try
                {
                    using (var refreshScope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, AccessLevel.System))
                    {
                        refreshScope.SaveChanges();
                    }
                    results.Add(new { step = "Refresh metamodel cache", success = true });
                }
                catch (Exception ex)
                {
                    results.Add(new { step = "Refresh metamodel cache", success = false, error = ex.Message });
                }

                return Ok(new
                {
                    success = true,
                    message = "Cleanup completed. Test field removed from all locations.",
                    cleanupResults = results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Show a summary of the full reproduction workflow and the current state.
        /// Sample usage: https://localhost:5009/util-api/custom-meta-field-deployment/summary
        /// </summary>
        [HttpGet("summary")]
        public IActionResult Summary()
        {
            try
            {
                var metaClass = DataContext.Current.MetaModel.MetaClasses[OrganizationClassName];
                var fieldInCache = metaClass?.Fields.Contains(TestFieldName) ?? false;

                var metaFieldRowExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $@"SELECT 1 FROM mcmd_MetaField mf 
                       INNER JOIN mcmd_MetaClass mc ON mf.MetaClassId = mc.MetaClassId 
                       WHERE mc.Name = '{OrganizationClassName}' AND mf.Name = '{TestFieldName}'"))
                {
                    metaFieldRowExists = reader.Read();
                }

                var sqlColumnExists = false;
                using (var reader = SqlHelper.ExecuteReader(
                    SqlContext.Current,
                    CommandType.Text,
                    $"SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{OrganizationTableName}' AND COLUMN_NAME = '{TestFieldName}'"))
                {
                    sqlColumnExists = reader.Read();
                }

                return Ok(new
                {
                    success = true,
                    testFieldName = TestFieldName,
                    currentState = new
                    {
                        fieldInMetamodelCache = fieldInCache,
                        mcmdMetaFieldRowExists = metaFieldRowExists,
                        sqlColumnExists,
                        isHealthy = fieldInCache == metaFieldRowExists && metaFieldRowExists == sqlColumnExists,
                        isOrphan = sqlColumnExists && !metaFieldRowExists
                    },
                    reproductionWorkflow = new[]
                    {
                        "Step 1: GET /util-api/custom-meta-field-deployment/inspect-organization — See current Organization state",
                        "Step 2: GET /util-api/custom-meta-field-deployment/add-field-standard — Add test field (non-idempotent way)",
                        "Step 3: GET /util-api/custom-meta-field-deployment/simulate-orphan-column — Delete mcmd_MetaField row, keep SQL column",
                        "Step 4: GET /util-api/custom-meta-field-deployment/trigger-sql-2705 — Re-add field → SQL 2705 error",
                        "Step 5: GET /util-api/custom-meta-field-deployment/add-field-safe — Demonstrate idempotent guard pattern",
                        "Step 6: GET /util-api/custom-meta-field-deployment/cleanup — Remove test field completely",
                        "Step 7: GET /util-api/custom-meta-field-deployment/summary — This endpoint"
                    },
                    rootCauseAnalysis = new
                    {
                        issue = "Table.AddColumn emits ALTER TABLE ADD [col] without IF NOT EXISTS guard",
                        trigger = "DDL + mcmd_MetaField insert are not transactionally atomic; if the insert fails the column persists as an orphan",
                        inMemoryGuard = "MetaClass.CreateMetaField checks Fields.Contains(name) but this uses cached data from mcmd_MetaField — if the row is missing, it passes",
                        multiInstance = "Static _lockObject in AutoInstallMetaDataModule only serializes within one process; DXP web + slot + scheduler are separate processes"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }
    }
}
