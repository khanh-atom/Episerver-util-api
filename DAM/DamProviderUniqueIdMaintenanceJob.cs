using System.Data;
using System.Globalization;
using System.Text;
using EPiServer.Data;
using EPiServer.PlugIn;
using EPiServer.Scheduler;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Foundation.Custom.DAM;

/// <summary>
/// Scheduled job that fixes stale <c>ProviderUniqueId</c> values in <c>tblMappedIdentity</c> for DAM assets.
///
/// Background:
/// In some configurations, DAM mapped identities can be created with a rendition GUID as the
/// <c>ProviderUniqueId</c> instead of the correct original asset URL. The standard metadata
/// maintenance job only refreshes the <c>Metadata</c> JSON column, but the Content Delivery API
/// resolves DAM URLs directly from <c>ProviderUniqueId</c>. This means the CD API continues to
/// return incorrect image URLs even after metadata is refreshed.
///
/// This job detects and corrects those stale entries so the CD API returns the proper asset URL.
///
/// How it works:
/// 1. Lists all DAM entries in <c>tblMappedIdentity</c>
/// 2. Identifies entries where the URL ends with a raw 32-character hex GUID (rendition ID)
///    instead of a base64-encoded original asset ID (e.g. <c>Zz0x...==</c>)
/// 3. For each stale entry, resolves the correct original asset URL from the stored metadata
/// 4. Updates <c>ProviderUniqueId</c> in <c>tblMappedIdentity</c> to the corrected URL
///
/// Pre-requisites:
/// - The standard "Optimizely CMP DAM asset metadata maintenance" job should be run first
///   so that <c>DAMAssetInfo</c> is populated with the correct asset URL in the Metadata column.
/// </summary>
[ScheduledPlugIn(
    DisplayName = "[ProviderUniqueId] Optimizely CMP DAM assets maintenance",
    Description = "Detects and corrects stale rendition-based ProviderUniqueId values in tblMappedIdentity for DAM assets. " +
                  "Run after the standard DAM metadata maintenance job to ensure the Content Delivery API returns correct image URLs.",
    GUID = "A7F3B2C1-4D5E-6F78-9A0B-1C2D3E4F5A6B",
    SortIndex = 10100)]
public class DamProviderUniqueIdMaintenanceJob : ScheduledJobBase
{
    private readonly IDatabaseExecutor _databaseExecutor;
    private readonly ILogger<DamProviderUniqueIdMaintenanceJob> _logger;
    private bool _stopSignaled;
    private int _totalProcessed;
    private int _totalFixed;
    private int _totalSkipped;
    private int _totalErrors;

    public DamProviderUniqueIdMaintenanceJob(
        IDatabaseExecutor databaseExecutor,
        ILogger<DamProviderUniqueIdMaintenanceJob> logger)
    {
        _databaseExecutor = databaseExecutor;
        _logger = logger;
        IsStoppable = true;
    }

    public override string Execute()
    {
        _stopSignaled = false;
        _totalProcessed = 0;
        _totalFixed = 0;
        _totalSkipped = 0;
        _totalErrors = 0;

        OnStatusChanged("Starting [ProviderUniqueId] DAM assets maintenance...");
        _logger.LogInformation("[ProviderUniqueId] DAM assets maintenance job started.");

        try
        {
            // Step 1: Load all stale DAM mapped identities from the database
            var staleEntries = LoadStaleDamEntries();
            OnStatusChanged($"Found {staleEntries.Count} DAM entries with potential stale rendition URLs.");
            _logger.LogInformation("Found {Count} DAM entries with potential stale rendition URLs.", staleEntries.Count);

            if (staleEntries.Count == 0)
            {
                return "No stale DAM ProviderUniqueId entries found. All entries appear correct.";
            }

            // Step 2: Process each stale entry
            foreach (var entry in staleEntries)
            {
                if (_stopSignaled)
                {
                    _logger.LogInformation("Job stopped by user after processing {Count} entries.", _totalProcessed);
                    break;
                }

                try
                {
                    ProcessStaleEntry(entry);
                    _totalProcessed++;

                    if (_totalProcessed % 10 == 0)
                    {
                        OnStatusChanged(string.Format(CultureInfo.InvariantCulture,
                            "Processed {0}/{1} entries. Fixed: {2}, Skipped: {3}, Errors: {4}",
                            _totalProcessed, staleEntries.Count, _totalFixed, _totalSkipped, _totalErrors));
                    }
                }
                catch (Exception ex)
                {
                    _totalErrors++;
                    _logger.LogError(ex, "Error processing stale entry pkID={PkID}, ContentGuid={ContentGuid}",
                        entry.PkID, entry.ContentGuid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProviderUniqueId] DAM assets maintenance job failed.");
            throw;
        }

        var result = string.Format(CultureInfo.InvariantCulture,
            "[ProviderUniqueId] DAM assets maintenance completed. " +
            "Total: {0}, Fixed: {1}, Skipped: {2}, Errors: {3}",
            _totalProcessed, _totalFixed, _totalSkipped, _totalErrors);

        _logger.LogInformation(result);
        return result;
    }

    public override void Stop()
    {
        _stopSignaled = true;
        base.Stop();
    }

    /// <summary>
    /// Loads all DAM entries from tblMappedIdentity that have a hex GUID as the last URL segment
    /// (indicating a rendition ID was stored instead of the original base64-encoded asset ID).
    ///
    /// Correct URL pattern:  .../imagename.jpg/Zz0xMGUyOGFlOGZkNz...== (base64)
    /// Stale URL pattern:    .../imagename.jpg/a50c7ec8ae5311efa0fd22b345a1d0dd (hex GUID)
    /// </summary>
    private List<StaleDamEntry> LoadStaleDamEntries()
    {
        var entries = new List<StaleDamEntry>();

        _databaseExecutor.Execute(() =>
        {
            using var command = _databaseExecutor.CreateCommand();
            command.CommandText = @"
                SELECT pkID, ContentGuid, ProviderUniqueId, Metadata
                FROM dbo.tblMappedIdentity
                WHERE Provider = 'dam'
                  AND ProviderUniqueId LIKE '%images%'
                  AND ProviderUniqueId NOT LIKE '%Zz%3D%3D'
                  AND ProviderUniqueId NOT LIKE '%Zz%3d%3d'
                  AND ProviderUniqueId NOT LIKE '%==%'
                  AND ProviderUniqueId NOT LIKE '%checkExpiry%'
                ORDER BY pkID";

            command.CommandType = CommandType.Text;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new StaleDamEntry
                {
                    PkID = reader.GetInt32(0),
                    ContentGuid = reader.GetGuid(1),
                    ProviderUniqueId = reader.GetString(2),
                    Metadata = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
        });

        return entries;
    }

    /// <summary>
    /// Processes a single stale entry:
    /// 1. Decodes the ProviderUniqueId to extract the base URL and stale rendition segment
    /// 2. Extracts the rendition GUID from the last URL segment
    /// 3. Constructs the correct original asset URL using the base64-encoded asset ID from Metadata
    /// 4. Updates tblMappedIdentity with the corrected ProviderUniqueId
    /// </summary>
    private void ProcessStaleEntry(StaleDamEntry entry)
    {
        // Decode the URL-encoded ProviderUniqueId
        var decodedUrl = Uri.UnescapeDataString(entry.ProviderUniqueId);

        // Extract the last segment (the stale rendition GUID)
        var uri = new Uri(decodedUrl);
        var segments = uri.Segments;
        if (segments.Length < 2)
        {
            _logger.LogWarning("Entry pkID={PkID} has unexpected URL structure: {Url}", entry.PkID, decodedUrl);
            _totalSkipped++;
            return;
        }

        var lastSegment = segments[^1].TrimEnd('/');

        // Verify it looks like a hex GUID (32 hex chars, no hyphens)
        if (!IsHexGuid(lastSegment))
        {
            _logger.LogDebug("Entry pkID={PkID} last segment is not a hex GUID, skipping: {Segment}",
                entry.PkID, lastSegment);
            _totalSkipped++;
            return;
        }

        // Try to parse the rendition GUID
        if (!Guid.TryParse(lastSegment, out var renditionGuid))
        {
            _logger.LogWarning("Entry pkID={PkID} has invalid rendition GUID: {Segment}", entry.PkID, lastSegment);
            _totalSkipped++;
            return;
        }

        // Try to extract the OriginalAssetId from the Metadata JSON
        string? originalAssetId = TryExtractOriginalAssetIdFromMetadata(entry.Metadata);

        // If we have an OriginalAssetId in metadata, use it to construct the correct URL
        // Otherwise, we need to use the rendition GUID to look up the original asset
        string? correctBase64Segment = null;

        if (!string.IsNullOrEmpty(originalAssetId) && Guid.TryParse(originalAssetId, out var originalGuid))
        {
            // Construct the base64 segment from the original asset GUID
            correctBase64Segment = ConvertGuidToBase64Segment(originalGuid);
        }

        // Also try extracting from the DAMAssetInfo.Url in metadata if available
        if (string.IsNullOrEmpty(correctBase64Segment))
        {
            correctBase64Segment = TryExtractBase64SegmentFromMetadataUrl(entry.Metadata);
        }

        if (string.IsNullOrEmpty(correctBase64Segment))
        {
            _logger.LogWarning(
                "Entry pkID={PkID} (ContentGuid={ContentGuid}): Cannot determine correct original asset URL. " +
                "RenditionGuid={RenditionGuid}. Manual intervention required.",
                entry.PkID, entry.ContentGuid, renditionGuid);
            _totalSkipped++;
            return;
        }

        // Construct the corrected ProviderUniqueId by replacing the last segment
        var baseUrl = decodedUrl[..decodedUrl.LastIndexOf('/') ];
        var correctedUrl = $"{baseUrl}/{correctBase64Segment}";
        var correctedProviderUniqueId = Uri.EscapeDataString(correctedUrl);

        // Update the database
        UpdateProviderUniqueId(entry.PkID, entry.ContentGuid, correctedProviderUniqueId);

        _logger.LogInformation(
            "Fixed pkID={PkID} (ContentGuid={ContentGuid}): " +
            "Old rendition segment={OldSegment}, New base64 segment={NewSegment}",
            entry.PkID, entry.ContentGuid, lastSegment, correctBase64Segment);

        _totalFixed++;
    }

    /// <summary>
    /// Updates the ProviderUniqueId for a specific entry in tblMappedIdentity.
    /// </summary>
    private void UpdateProviderUniqueId(int pkId, Guid contentGuid, string newProviderUniqueId)
    {
        _databaseExecutor.Execute(() =>
        {
            using var command = _databaseExecutor.CreateCommand();
            command.CommandText = @"
                UPDATE dbo.tblMappedIdentity
                SET ProviderUniqueId = @NewProviderUniqueId,
                    Saved = GETUTCDATE()
                WHERE pkID = @PkID AND ContentGuid = @ContentGuid";

            command.CommandType = CommandType.Text;

            var paramNew = command.CreateParameter();
            paramNew.ParameterName = "@NewProviderUniqueId";
            paramNew.Value = newProviderUniqueId;
            command.Parameters.Add(paramNew);

            var paramPk = command.CreateParameter();
            paramPk.ParameterName = "@PkID";
            paramPk.Value = pkId;
            command.Parameters.Add(paramPk);

            var paramGuid = command.CreateParameter();
            paramGuid.ParameterName = "@ContentGuid";
            paramGuid.Value = contentGuid;
            command.Parameters.Add(paramGuid);

            var rowsAffected = command.ExecuteNonQuery();
            if (rowsAffected != 1)
            {
                _logger.LogWarning(
                    "Expected 1 row affected when updating pkID={PkID}, but got {RowsAffected}",
                    pkId, rowsAffected);
            }
        });
    }

    /// <summary>
    /// Checks if a string looks like a 32-character hex GUID (without hyphens).
    /// Example: a50c7ec8ae5311efa0fd22b345a1d0dd
    /// </summary>
    private static bool IsHexGuid(string segment)
    {
        if (segment.Length != 32)
            return false;

        foreach (var c in segment)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a GUID to the base64 segment format used in DAM original asset URLs.
    /// Format: Zz={GUID_hex_no_hyphens} → Base64 encoded → URL-safe
    /// Example: GUID 80a8ac82ae5311ef851f5a247d527078
    ///          → "g=80a8ac82ae5311ef851f5a247d527078"
    ///          → Base64: "Zz04MGE4YWM4MmFlNTMxMWVmODUxZjVhMjQ3ZDUyNzA3OA=="
    /// </summary>
    private static string ConvertGuidToBase64Segment(Guid assetGuid)
    {
        var hexString = assetGuid.ToString("N"); // 32 hex chars, no hyphens
        var rawValue = $"g={hexString}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rawValue));
    }

    /// <summary>
    /// Tries to extract OriginalAssetId from the Metadata JSON string.
    /// The metadata JSON structure is: {"Title":"...","Type":0,"OriginalAssetId":"...","DAMAssetInfo":{...}}
    /// </summary>
    private static string? TryExtractOriginalAssetIdFromMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        // Simple JSON parsing - looking for "OriginalAssetId":"value"
        var key = "\"OriginalAssetId\":\"";
        var startIndex = metadataJson.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += key.Length;
        var endIndex = metadataJson.IndexOf('"', startIndex);
        if (endIndex < 0)
            return null;

        var value = metadataJson[startIndex..endIndex];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Tries to extract the base64 segment from the DAMAssetInfo.Url in metadata.
    /// If metadata contains the correct URL (e.g., from a successful metadata refresh),
    /// we can extract the last segment as the correct base64-encoded asset ID.
    /// </summary>
    private static string? TryExtractBase64SegmentFromMetadataUrl(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        // Look for "Url":"https://..." in the DAMAssetInfo section
        var key = "\"Url\":\"";
        var startIndex = metadataJson.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += key.Length;
        var endIndex = metadataJson.IndexOf('"', startIndex);
        if (endIndex < 0)
            return null;

        var urlValue = metadataJson[startIndex..endIndex]
            .Replace("\\u002B", "+")
            .Replace("\\u0027", "'");

        if (string.IsNullOrWhiteSpace(urlValue))
            return null;

        try
        {
            var uri = new Uri(urlValue);
            var lastSegment = uri.Segments[^1].TrimEnd('/');

            // Check if the last segment looks like a base64-encoded value
            if (lastSegment.Contains("Zz") || lastSegment.EndsWith("=="))
            {
                return lastSegment;
            }
        }
        catch
        {
            // URL parsing failed - not extractable
        }

        return null;
    }

    /// <summary>
    /// Represents a DAM entry in tblMappedIdentity that has a potentially stale ProviderUniqueId.
    /// </summary>
    private class StaleDamEntry
    {
        public int PkID { get; set; }
        public Guid ContentGuid { get; set; }
        public string ProviderUniqueId { get; set; } = string.Empty;
        public string? Metadata { get; set; }
    }
}
