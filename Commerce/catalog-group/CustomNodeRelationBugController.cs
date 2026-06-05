using EPiServer.Commerce.Catalog.Linking;
using EPiServer.DataAccess;
using EPiServer.Security;
using Foundation.Features.Search.Category;
using Mediachase.Commerce.Catalog.Dto;
using Mediachase.Commerce.Catalog.Managers;
using System.Data;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.CatalogGroup
{
    /// <summary>
    /// API to reproduce and investigate the CatalogNodeRelation orphan bug.
    /// When nodes are moved between catalogs via IContentRepository.Move(),
    /// the CatalogNodeRelation.CatalogId is NOT updated, leaving orphaned FK references
    /// that block catalog deletion.
    ///
    /// Steps to reproduce:
    /// 1. /util-api/custom-node-relation-bug/step1-setup — Creates two catalogs with categories and a node relation link
    /// 2. /util-api/custom-node-relation-bug/step2-verify-before-move — Shows the CatalogNodeRelation state before move
    /// 3. /util-api/custom-node-relation-bug/step3-move-nodes — Moves nodes from OldCatalog to NewCatalog
    /// 4. /util-api/custom-node-relation-bug/step4-verify-after-move — Shows orphaned CatalogNodeRelation rows (the bug)
    /// 5. /util-api/custom-node-relation-bug/step5-try-delete-catalog — Attempts to delete OldCatalog (will fail with FK error)
    /// 6. /util-api/custom-node-relation-bug/step6-fix-and-delete — Fixes orphaned rows then deletes the catalog
    /// 7. /util-api/custom-node-relation-bug/cleanup — Removes all test data
    /// </summary>
    [ApiController]
    [Route("util-api/custom-node-relation-bug")]
    public class CustomNodeRelationBugController : ControllerBase
    {
        private readonly IContentRepository _contentRepository;
        private readonly ReferenceConverter _referenceConverter;
        private readonly IRelationRepository _relationRepository;
        private readonly ICatalogSystem _catalogSystem;

        private const string OldCatalogName = "BugTest_OldCatalog";
        private const string NewCatalogName = "BugTest_NewCatalog";
        private const string ParentNodeCode = "BugTest_ParentNode";
        private const string ChildNodeCode = "BugTest_ChildNode";
        private const string LinkedNodeCode = "BugTest_LinkedNode";

        public CustomNodeRelationBugController()
        {
            _contentRepository = ServiceLocator.Current.GetInstance<IContentRepository>();
            _referenceConverter = ServiceLocator.Current.GetInstance<ReferenceConverter>();
            _relationRepository = ServiceLocator.Current.GetInstance<IRelationRepository>();
            _catalogSystem = ServiceLocator.Current.GetInstance<ICatalogSystem>();
        }

        /// <summary>
        /// Step 1: Setup test data — creates two catalogs, three categories, and a node relation link.
        /// Creates OldCatalog with ParentNode (containing ChildNode), plus a LinkedNode linked as "additional category" under ParentNode.
        /// Creates an empty NewCatalog as the move target.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step1-setup
        /// </summary>
        [HttpGet("step1-setup")]
        public IActionResult Step1Setup()
        {
            try
            {
                var rootLink = _referenceConverter.GetRootLink();
                var results = new List<object>();

                // --- Create OldCatalog ---
                var oldCatalogLink = GetOrCreateCatalog(OldCatalogName, rootLink);
                results.Add(new { step = "OldCatalog", name = OldCatalogName, contentLink = oldCatalogLink.ToString() });

                // --- Create NewCatalog ---
                var newCatalogLink = GetOrCreateCatalog(NewCatalogName, rootLink);
                results.Add(new { step = "NewCatalog", name = NewCatalogName, contentLink = newCatalogLink.ToString() });

                // --- Create ParentNode under OldCatalog ---
                var parentNodeLink = GetOrCreateNode(ParentNodeCode, "Parent Category", oldCatalogLink);
                results.Add(new { step = "ParentNode", code = ParentNodeCode, contentLink = parentNodeLink.ToString() });

                // --- Create ChildNode under ParentNode ---
                var childNodeLink = GetOrCreateNode(ChildNodeCode, "Child Category", parentNodeLink);
                results.Add(new { step = "ChildNode", code = ChildNodeCode, contentLink = childNodeLink.ToString() });

                // --- Create LinkedNode under OldCatalog root (will be linked as additional category under ParentNode) ---
                var linkedNodeLink = GetOrCreateNode(LinkedNodeCode, "Linked Category", oldCatalogLink);
                results.Add(new { step = "LinkedNode", code = LinkedNodeCode, contentLink = linkedNodeLink.ToString() });

                // --- Create the "additional category" link: LinkedNode linked under ParentNode ---
                // This creates a CatalogNodeRelation row
                var existingChildren = _relationRepository.GetChildren<NodeRelation>(parentNodeLink).ToList();
                var alreadyLinked = existingChildren.Any(r => r.Child.CompareToIgnoreWorkID(linkedNodeLink));

                if (!alreadyLinked)
                {
                    var nodeRelation = new NodeRelation
                    {
                        Parent = parentNodeLink,
                        Child = linkedNodeLink,
                        SortOrder = 100
                    };
                    _relationRepository.UpdateRelation(nodeRelation);
                    results.Add(new { step = "NodeRelation", parent = ParentNodeCode, child = LinkedNodeCode, status = "Created" });
                }
                else
                {
                    results.Add(new { step = "NodeRelation", parent = ParentNodeCode, child = LinkedNodeCode, status = "AlreadyExists" });
                }

                return Ok(new
                {
                    message = "Step 1 complete: Test data created",
                    nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/step2-verify-before-move",
                    results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Verify the CatalogNodeRelation state BEFORE moving nodes.
        /// Shows that CatalogNodeRelation.CatalogId matches the OldCatalog.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step2-verify-before-move
        /// </summary>
        [HttpGet("step2-verify-before-move")]
        public IActionResult Step2VerifyBeforeMove()
        {
            try
            {
                var oldCatalogId = GetCatalogIdByName(OldCatalogName);
                var newCatalogId = GetCatalogIdByName(NewCatalogName);

                if (oldCatalogId == 0)
                    return NotFound(new { error = $"Catalog '{OldCatalogName}' not found. Run step1-setup first." });

                // Load CatalogNodeRelation rows for the old catalog
                var relationDto = _catalogSystem.GetCatalogRelationDto(
                    oldCatalogId, 0, 0, string.Empty,
                    new CatalogRelationResponseGroup(CatalogRelationResponseGroup.ResponseGroup.CatalogNode));

                var nodeRelations = relationDto.CatalogNodeRelation
                    .Select(r => new
                    {
                        CatalogId = r.CatalogId,
                        ParentNodeId = r.ParentNodeId,
                        ChildNodeId = r.ChildNodeId,
                        SortOrder = r.SortOrder
                    }).ToList();

                // Load CatalogNode info
                var nodeDto = _catalogSystem.GetCatalogNodesDto(oldCatalogId);
                var nodes = nodeDto.CatalogNode
                    .Select(n => new
                    {
                        CatalogNodeId = n.CatalogNodeId,
                        CatalogId = n.CatalogId,
                        ParentNodeId = n.ParentNodeId,
                        Name = n.Name,
                        Code = n.Code
                    }).ToList();

                return Ok(new
                {
                    message = "Step 2: State BEFORE move",
                    oldCatalogId,
                    newCatalogId,
                    catalogNodeRelationRows = nodeRelations,
                    catalogNodeRelationCount = nodeRelations.Count,
                    nodesInOldCatalog = nodes,
                    nodesInOldCatalogCount = nodes.Count,
                    explanation = "CatalogNodeRelation.CatalogId should match the OldCatalog ID. This is correct at this point.",
                    nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/step3-move-nodes"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Move all nodes from OldCatalog to NewCatalog using IContentRepository.Move().
        /// This triggers the bug — CatalogNode.CatalogId is updated, but CatalogNodeRelation.CatalogId is NOT.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step3-move-nodes
        /// </summary>
        [HttpGet("step3-move-nodes")]
        public IActionResult Step3MoveNodes()
        {
            try
            {
                var rootLink = _referenceConverter.GetRootLink();
                var catalogs = _contentRepository.GetChildren<CatalogContent>(rootLink);

                var oldCatalog = catalogs.FirstOrDefault(c => c.Name == OldCatalogName);
                var newCatalog = catalogs.FirstOrDefault(c => c.Name == NewCatalogName);

                if (oldCatalog == null)
                    return NotFound(new { error = $"Catalog '{OldCatalogName}' not found. Run step1-setup first." });
                if (newCatalog == null)
                    return NotFound(new { error = $"Catalog '{NewCatalogName}' not found. Run step1-setup first." });

                // Get top-level nodes from old catalog
                var topLevelNodes = _contentRepository.GetChildren<NodeContent>(oldCatalog.ContentLink).ToList();
                var moveResults = new List<object>();

                foreach (var node in topLevelNodes)
                {
                    _contentRepository.Move(node.ContentLink, newCatalog.ContentLink, AccessLevel.NoAccess, AccessLevel.NoAccess);
                    moveResults.Add(new
                    {
                        nodeName = node.Name,
                        nodeCode = node.Code,
                        from = OldCatalogName,
                        to = NewCatalogName,
                        status = "Moved"
                    });
                }

                return Ok(new
                {
                    message = "Step 3 complete: Nodes moved from OldCatalog to NewCatalog",
                    movedNodes = moveResults,
                    explanation = "IContentRepository.Move() updates CatalogNode.CatalogId but does NOT update CatalogNodeRelation.CatalogId. " +
                                  "The 'additional category' link still references the OLD catalog ID.",
                    nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/step4-verify-after-move"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Verify the CatalogNodeRelation state AFTER moving nodes.
        /// This reveals the bug: CatalogNodeRelation.CatalogId still references the old catalog,
        /// while the nodes themselves have moved to the new catalog.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step4-verify-after-move
        /// </summary>
        [HttpGet("step4-verify-after-move")]
        public IActionResult Step4VerifyAfterMove()
        {
            try
            {
                var oldCatalogId = GetCatalogIdByName(OldCatalogName);
                var newCatalogId = GetCatalogIdByName(NewCatalogName);

                if (oldCatalogId == 0)
                    return NotFound(new { error = $"Catalog '{OldCatalogName}' not found. Run step1-setup first." });

                // Load ALL CatalogNodeRelation rows (unfiltered) to find orphans
                var allRelationDto = _catalogSystem.GetCatalogRelationDto(
                    0, 0, 0, string.Empty,
                    new CatalogRelationResponseGroup(CatalogRelationResponseGroup.ResponseGroup.CatalogNode));

                var orphanedRelations = allRelationDto.CatalogNodeRelation
                    .Where(r => r.CatalogId == oldCatalogId)
                    .Select(r => new
                    {
                        CatalogId_InRelation = r.CatalogId,
                        ParentNodeId = r.ParentNodeId,
                        ChildNodeId = r.ChildNodeId,
                        SortOrder = r.SortOrder
                    }).ToList();

                // Check where the parent nodes actually are now
                var nodeInfos = new List<object>();
                foreach (var orphan in orphanedRelations)
                {
                    var parentDto = _catalogSystem.GetCatalogNodeDto(orphan.ParentNodeId);
                    var childDto = _catalogSystem.GetCatalogNodeDto(orphan.ChildNodeId);
                    nodeInfos.Add(new
                    {
                        ParentNodeId = orphan.ParentNodeId,
                        ParentNode_ActualCatalogId = parentDto.CatalogNode.Count > 0 ? parentDto.CatalogNode[0].CatalogId : (int?)null,
                        ParentNode_Name = parentDto.CatalogNode.Count > 0 ? parentDto.CatalogNode[0].Name : null,
                        ChildNodeId = orphan.ChildNodeId,
                        ChildNode_ActualCatalogId = childDto.CatalogNode.Count > 0 ? childDto.CatalogNode[0].CatalogId : (int?)null,
                        ChildNode_Name = childDto.CatalogNode.Count > 0 ? childDto.CatalogNode[0].Name : null,
                        CatalogNodeRelation_CatalogId = orphan.CatalogId_InRelation,
                        isBug = parentDto.CatalogNode.Count > 0 && orphan.CatalogId_InRelation != parentDto.CatalogNode[0].CatalogId
                    });
                }

                // Nodes remaining in old catalog
                var oldCatalogNodesDto = _catalogSystem.GetCatalogNodesDto(oldCatalogId);
                var nodesInOldCatalog = oldCatalogNodesDto.CatalogNode
                    .Select(n => new { n.CatalogNodeId, n.Name, n.CatalogId }).ToList();

                // Nodes in new catalog
                var newCatalogNodesDto = _catalogSystem.GetCatalogNodesDto(newCatalogId);
                var nodesInNewCatalog = newCatalogNodesDto.CatalogNode
                    .Select(n => new { n.CatalogNodeId, n.Name, n.CatalogId }).ToList();

                return Ok(new
                {
                    message = "Step 4: State AFTER move — THE BUG IS VISIBLE",
                    oldCatalogId,
                    newCatalogId,
                    orphanedCatalogNodeRelations = orphanedRelations,
                    orphanedCount = orphanedRelations.Count,
                    nodeDetails = nodeInfos,
                    nodesRemainingInOldCatalog = nodesInOldCatalog,
                    nodesInNewCatalog = nodesInNewCatalog,
                    bugExplanation = "CatalogNodeRelation.CatalogId is still " + oldCatalogId +
                                     " (OldCatalog), but the actual nodes now belong to CatalogId " + newCatalogId +
                                     " (NewCatalog). This mismatch is the product bug in CatalogContentMoveHandler.MoveCatalogNode().",
                    nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/step5-try-delete-catalog"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Attempt to delete OldCatalog — this will FAIL with FK_CatalogItemCategory_Catalog violation.
        /// This demonstrates the exact error the customer experiences.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step5-try-delete-catalog
        /// </summary>
        [HttpGet("step5-try-delete-catalog")]
        public IActionResult Step5TryDeleteCatalog()
        {
            try
            {
                var rootLink = _referenceConverter.GetRootLink();
                var oldCatalog = _contentRepository.GetChildren<CatalogContent>(rootLink)
                    .FirstOrDefault(c => c.Name == OldCatalogName);

                if (oldCatalog == null)
                    return NotFound(new { error = $"Catalog '{OldCatalogName}' not found." });

                var oldCatalogId = GetCatalogIdByName(OldCatalogName);

                // Check for orphaned relations first
                var allRelationDto = _catalogSystem.GetCatalogRelationDto(
                    0, 0, 0, string.Empty,
                    new CatalogRelationResponseGroup(CatalogRelationResponseGroup.ResponseGroup.CatalogNode));
                var orphanCount = allRelationDto.CatalogNodeRelation.Count(r => r.CatalogId == oldCatalogId);

                // Try to delete — this should fail
                try
                {
                    _contentRepository.Delete(oldCatalog.ContentLink, true, AccessLevel.NoAccess);
                    return Ok(new
                    {
                        message = "Catalog deleted successfully (unexpected — the bug may have been fixed in this version)",
                        catalogId = oldCatalogId
                    });
                }
                catch (Exception deleteEx)
                {
                    var isFkViolation = deleteEx.ToString().Contains("FK_CatalogItemCategory_Catalog")
                                        || deleteEx.ToString().Contains("CatalogNodeRelation");
                    return Ok(new
                    {
                        message = "Step 5: Delete FAILED as expected — FK constraint violation",
                        catalogId = oldCatalogId,
                        orphanedCatalogNodeRelationCount = orphanCount,
                        isFkConstraintError = isFkViolation,
                        errorType = deleteEx.GetType().Name,
                        errorMessage = deleteEx.Message,
                        innerErrorMessage = deleteEx.InnerException?.Message,
                        explanation = "The DELETE fails because CatalogNodeRelation rows still reference CatalogId=" + oldCatalogId +
                                      ". The cleanup code in CatalogManager.DeleteCatalog() cannot find these rows because " +
                                      "it JOINs CatalogNodeRelation with CatalogNode on ParentNodeId, but the parent nodes " +
                                      "have already moved to the new catalog.",
                        nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/step6-fix-and-delete"
                    });
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Fix the orphaned CatalogNodeRelation rows by removing them via CatalogRelationDto, then delete the catalog.
        /// This is the workaround for the product bug.
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/step6-fix-and-delete
        /// </summary>
        [HttpGet("step6-fix-and-delete")]
        public IActionResult Step6FixAndDelete()
        {
            try
            {
                var oldCatalogId = GetCatalogIdByName(OldCatalogName);
                if (oldCatalogId == 0)
                    return NotFound(new { error = $"Catalog '{OldCatalogName}' not found." });

                // Load all CatalogNodeRelation rows (unfiltered by catalog) to find orphans
                var allRelationDto = _catalogSystem.GetCatalogRelationDto(
                    0, 0, 0, string.Empty,
                    new CatalogRelationResponseGroup(CatalogRelationResponseGroup.ResponseGroup.CatalogNode));

                var orphanedRows = allRelationDto.CatalogNodeRelation
                    .Where(r => r.CatalogId == oldCatalogId).ToList();

                var fixResults = new List<object>();

                // Delete orphaned rows via DTO
                if (orphanedRows.Any())
                {
                    foreach (var row in orphanedRows)
                    {
                        fixResults.Add(new
                        {
                            action = "DeleteOrphanedRow",
                            CatalogId = row.CatalogId,
                            ParentNodeId = row.ParentNodeId,
                            ChildNodeId = row.ChildNodeId
                        });
                        row.Delete();
                    }
                    _catalogSystem.SaveCatalogRelationDto(allRelationDto);
                }

                // Now try to delete the catalog
                var rootLink = _referenceConverter.GetRootLink();
                var oldCatalog = _contentRepository.GetChildren<CatalogContent>(rootLink)
                    .FirstOrDefault(c => c.Name == OldCatalogName);

                if (oldCatalog != null)
                {
                    _contentRepository.Delete(oldCatalog.ContentLink, true, AccessLevel.NoAccess);
                    fixResults.Add(new { action = "DeleteCatalog", name = OldCatalogName, status = "Success" });
                }

                return Ok(new
                {
                    message = "Step 6 complete: Orphaned rows fixed and catalog deleted",
                    fixedOrphanCount = orphanedRows.Count,
                    actions = fixResults,
                    nextStep = "https://localhost:5009/util-api/custom-node-relation-bug/cleanup"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Cleanup: Remove all test data (both catalogs and their contents).
        /// Sample usage: https://localhost:5009/util-api/custom-node-relation-bug/cleanup
        /// </summary>
        [HttpGet("cleanup")]
        public IActionResult Cleanup()
        {
            try
            {
                var rootLink = _referenceConverter.GetRootLink();
                var catalogs = _contentRepository.GetChildren<CatalogContent>(rootLink).ToList();
                var cleanupResults = new List<object>();

                // Clean up orphaned CatalogNodeRelation rows first
                foreach (var catalogName in new[] { OldCatalogName, NewCatalogName })
                {
                    var catalogId = GetCatalogIdByName(catalogName);
                    if (catalogId > 0)
                    {
                        var allRelationDto = _catalogSystem.GetCatalogRelationDto(
                            0, 0, 0, string.Empty,
                            new CatalogRelationResponseGroup(CatalogRelationResponseGroup.ResponseGroup.CatalogNode));

                        var orphanedRows = allRelationDto.CatalogNodeRelation
                            .Where(r => r.CatalogId == catalogId).ToList();

                        if (orphanedRows.Any())
                        {
                            foreach (var row in orphanedRows)
                            {
                                row.Delete();
                            }
                            _catalogSystem.SaveCatalogRelationDto(allRelationDto);
                            cleanupResults.Add(new { action = "CleanOrphanedRelations", catalog = catalogName, count = orphanedRows.Count });
                        }
                    }
                }

                // Delete catalogs
                foreach (var catalogName in new[] { OldCatalogName, NewCatalogName })
                {
                    var catalog = catalogs.FirstOrDefault(c => c.Name == catalogName);
                    if (catalog != null)
                    {
                        _contentRepository.Delete(catalog.ContentLink, true, AccessLevel.NoAccess);
                        cleanupResults.Add(new { action = "DeleteCatalog", name = catalogName, status = "Deleted" });
                    }
                    else
                    {
                        cleanupResults.Add(new { action = "DeleteCatalog", name = catalogName, status = "NotFound" });
                    }
                }

                return Ok(new
                {
                    message = "Cleanup complete: All test data removed",
                    actions = cleanupResults
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Helper Methods

        private ContentReference GetOrCreateCatalog(string catalogName, ContentReference rootLink)
        {
            var catalogs = _contentRepository.GetChildren<CatalogContent>(rootLink);
            var existing = catalogs.FirstOrDefault(c => c.Name.Equals(catalogName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing.ContentLink;

            var catalog = _contentRepository.GetDefault<CatalogContent>(rootLink);
            catalog.Name = catalogName;
            catalog.DefaultCurrency = "USD";
            catalog.DefaultLanguage = "en";
            catalog.WeightBase = "kgs";
            catalog.LengthBase = "cm";
            catalog.CatalogLanguages = new ItemCollection<string> { "en" };
            var savedLink = _contentRepository.Save(catalog, SaveAction.Publish, AccessLevel.NoAccess);
            return savedLink;
        }

        private ContentReference GetOrCreateNode(string nodeCode, string nodeName, ContentReference parentLink)
        {
            var nodeLink = _referenceConverter.GetContentLink(nodeCode, CatalogContentType.CatalogNode);
            if (!ContentReference.IsNullOrEmpty(nodeLink))
                return nodeLink;

            var node = _contentRepository.GetDefault<GenericNode>(parentLink);
            node.Name = nodeName;
            node.Code = nodeCode;
            var savedLink = _contentRepository.Save(node, SaveAction.Publish, AccessLevel.NoAccess);
            return savedLink;
        }

        private int GetCatalogIdByName(string catalogName)
        {
            var allCatalogsDto = CatalogContext.Current.GetCatalogDto();
            var catalog = allCatalogsDto.Catalog
                .FirstOrDefault(c => c.Name.Equals(catalogName, StringComparison.OrdinalIgnoreCase));
            return catalog?.CatalogId ?? 0;
        }

        #endregion
    }
}
