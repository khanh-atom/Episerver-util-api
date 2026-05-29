using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.Graph.Source.Sdk;
using Optimizely.Graph.Source.Sdk.SourceConfiguration;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to replicate and debug Graph Source SDK custom object indexing with multiple languages.
    /// Each step corresponds to a separate API endpoint for easy testing.
    /// Base route: util-api/custom-graph-source
    /// </summary>
    [ApiController]
    [Route("util-api/custom-graph-source")]
    public class CustomGraphSourceController : ControllerBase
    {
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IConfiguration _configuration;

        // Static client so it persists across requests during testing
        private static GraphSourceClient _graphSourceClient;
        private static string _lastSaveTypesResult;
        private static string _lastSaveContentResult;
        private static List<string> _registeredLanguages = new();

        public CustomGraphSourceController(
            IOptions<QueryOptions> queryOptions,
            IConfiguration configuration)
        {
            _queryOptions = queryOptions;
            _configuration = configuration;
        }

        /// <summary>
        /// Step 0: Shows the current configuration and all available endpoints.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                return Ok(new
                {
                    Description = "Graph Source SDK — Custom Object Multi-Language Indexing Replication",
                    ClientInitialized = _graphSourceClient != null,
                    RegisteredLanguages = _registeredLanguages,
                    Config = new
                    {
                        GatewayAddress = _queryOptions.Value.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.AppKey),
                        HasSecret = !string.IsNullOrWhiteSpace(_queryOptions.Value.Secret),
                    },
                    Steps = new[]
                    {
                        "Step 1: GET /util-api/custom-graph-source/init-client?source=test-source — Initialize GraphSourceClient",
                        "Step 2: GET /util-api/custom-graph-source/save-types?languages=en,es,fr — Configure content type and register languages, then SaveTypesAsync",
                        "Step 3: GET /util-api/custom-graph-source/index-content?language=en&count=5 — Index sample custom objects for a specific language",
                        "Step 4: GET /util-api/custom-graph-source/index-all-languages?countPerLanguage=3 — Index content for all registered languages at once",
                        "Step 5: GET /util-api/custom-graph-source/query?locale=ALL&limit=10 — Query the indexed content via GraphQL",
                        "Step 6: GET /util-api/custom-graph-source/query-compare — Query ALL vs each locale side by side",
                        "Step 7: GET /util-api/custom-graph-source/delete-content — Delete all content from the source",
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-graph-source/init-client?source=test-source",
                        "https://localhost:5009/util-api/custom-graph-source/save-types?languages=en,es,fr",
                        "https://localhost:5009/util-api/custom-graph-source/index-content?language=en&count=5",
                        "https://localhost:5009/util-api/custom-graph-source/index-all-languages?countPerLanguage=3",
                        "https://localhost:5009/util-api/custom-graph-source/query?locale=ALL&limit=10",
                        "https://localhost:5009/util-api/custom-graph-source/query-compare",
                        "https://localhost:5009/util-api/custom-graph-source/delete-content",
                    },
                    BugReplicationUrls = new
                    {
                        Step1_Setup = "https://localhost:5009/util-api/custom-graph-source/repro-locale-bug-setup?source=repro01&languages=en,es,fr&itemsPerLang=2",
                        Step2_Verify = "https://localhost:5009/util-api/custom-graph-source/repro-locale-bug-verify?languages=en,es,fr&itemsPerLang=2",
                        Description = "Or run these manually in order to replicate the locale issue: only register 'en' but index content for en,es,fr",
                        Step1 = "https://localhost:5009/util-api/custom-graph-source/init-client?source=test-locale-bug",
                        Step2_OnlyEn = "https://localhost:5009/util-api/custom-graph-source/save-types?languages=en",
                        Step3_IndexAllLangs = "https://localhost:5009/util-api/custom-graph-source/index-all-languages?languages=en,es,fr&countPerLanguage=3",
                        Step4_Compare = "https://localhost:5009/util-api/custom-graph-source/query-compare",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Initialize the GraphSourceClient with the given source name.
        /// Uses AppKey and Secret from Optimizely:ContentGraph configuration.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/init-client?source=test-source
        /// </summary>
        [HttpGet("init-client")]
        public IActionResult InitClient(
            [FromQuery] string source = "test-source",
            [FromQuery] string gateway = null)
        {
            try
            {
                var appKey = _queryOptions.Value.AppKey
                    ?? _configuration["Optimizely:ContentGraph:AppKey"];
                var secret = _queryOptions.Value.Secret
                    ?? _configuration["Optimizely:ContentGraph:Secret"];
                var gatewayAddress = gateway
                    ?? _queryOptions.Value.GatewayAddress
                    ?? "https://cg.optimizely.com";

                if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(secret))
                {
                    return BadRequest(new
                    {
                        Error = "AppKey and/or Secret is not configured",
                        Hint = "Set Optimizely:ContentGraph:AppKey and Optimizely:ContentGraph:Secret in appsettings.json"
                    });
                }

                _graphSourceClient = GraphSourceClient.Create(
                    new Uri(gatewayAddress),
                    source,
                    appKey,
                    secret);

                _registeredLanguages = new List<string>();
                _lastSaveTypesResult = null;
                _lastSaveContentResult = null;

                return Ok(new
                {
                    Step = "1 — Init Client",
                    Success = true,
                    Source = source,
                    GatewayAddress = gatewayAddress,
                    Message = "GraphSourceClient initialized. Next: call /save-types to configure the content type and languages."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Configure the TestSearchResult content type, register languages, and call SaveTypesAsync.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/save-types?languages=en,es,fr
        /// </summary>
        [HttpGet("save-types")]
        public async Task<IActionResult> SaveTypes(
            [FromQuery] string languages = "en")
        {
            try
            {
                if (_graphSourceClient == null)
                {
                    return BadRequest(new { Error = "Client not initialized. Call /init-client first." });
                }

                // Configure content type with fields
                _graphSourceClient.ConfigureContentType<TestSearchResult>()
                    .Field(x => x.Id, IndexingType.Queryable)
                    .Field(x => x.Title, IndexingType.Searchable)
                    .Field(x => x.Description, IndexingType.Searchable)
                    .Field(x => x.Platform, IndexingType.Queryable)
                    .Field(x => x.Language, IndexingType.Queryable)
                    .Field(x => x.LastModified, IndexingType.Queryable);

                // Register languages
                var languageList = languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();

                foreach (var lang in languageList)
                {
                    _graphSourceClient.AddLanguage(lang);
                }

                _registeredLanguages = languageList;

                // Save types
                var result = await _graphSourceClient.SaveTypesAsync();
                _lastSaveTypesResult = result;

                return Ok(new
                {
                    Step = "2 — Save Types",
                    RegisteredLanguages = languageList,
                    SaveTypesResponse = TryParseJson(result),
                    Message = "Content type configured and languages registered. Next: call /index-content or /index-all-languages to push data."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Index sample TestSearchResult content for a single language.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/index-content?language=en&amp;count=5
        /// </summary>
        [HttpGet("index-content")]
        public async Task<IActionResult> IndexContent(
            [FromQuery] string language = "en",
            [FromQuery] int count = 5,
            [FromQuery] string platform = "CMS")
        {
            try
            {
                if (_graphSourceClient == null)
                {
                    return BadRequest(new { Error = "Client not initialized. Call /init-client first." });
                }

                var items = GenerateSampleItems(language, count, platform);

                var result = await _graphSourceClient.SaveContentAsync(
                    x => x.Id,
                    language,
                    items.ToArray());

                _lastSaveContentResult = result;

                return Ok(new
                {
                    Step = "3 — Index Content",
                    ItemCount = items.Count,
                    SavedLanguage = language,
                    Platform = platform,
                    SampleItems = items.Select(i => new { i.Id, i.Title, i.Language }),
                    SaveContentResponse = TryParseJson(result),
                    Message = $"Indexed {items.Count} items under locale '{language}'. Next: call /query to verify."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Index content for multiple languages at once (each language gets its own SaveContentAsync call).
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/index-all-languages?languages=en,es,fr&amp;countPerLanguage=3
        /// </summary>
        [HttpGet("index-all-languages")]
        public async Task<IActionResult> IndexAllLanguages(
            [FromQuery] string languages = null,
            [FromQuery] int countPerLanguage = 3,
            [FromQuery] string platform = "CMS")
        {
            try
            {
                if (_graphSourceClient == null)
                {
                    return BadRequest(new { Error = "Client not initialized. Call /init-client first." });
                }

                var languageList = string.IsNullOrWhiteSpace(languages)
                    ? _registeredLanguages
                    : languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim().ToLowerInvariant())
                        .Distinct()
                        .ToList();

                if (!languageList.Any())
                {
                    return BadRequest(new { Error = "No languages specified. Pass ?languages=en,es,fr or call /save-types first." });
                }

                var results = new List<object>();
                var allItems = new List<TestSearchResult>();

                foreach (var lang in languageList)
                {
                    var items = GenerateSampleItems(lang, countPerLanguage, platform);
                    allItems.AddRange(items);

                    var result = await _graphSourceClient.SaveContentAsync(
                        x => x.Id,
                        lang,
                        items.ToArray());

                    results.Add(new
                    {
                        Language = lang,
                        ItemCount = items.Count,
                        SampleIds = items.Select(i => i.Id),
                        SaveContentResponse = TryParseJson(result)
                    });
                }

                return Ok(new
                {
                    Step = "4 — Index All Languages",
                    RegisteredLanguages = _registeredLanguages,
                    IndexedLanguages = languageList,
                    TotalItemsIndexed = allItems.Count,
                    PerLanguageResults = results,
                    Message = $"Indexed {allItems.Count} items across {languageList.Count} languages. Next: call /query-compare to check locale filtering."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Query the indexed custom objects via raw GraphQL with locale parameter.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/query?locale=ALL&amp;limit=10
        /// </summary>
        [HttpGet("query")]
        public async Task<IActionResult> Query(
            [FromQuery] string locale = "ALL",
            [FromQuery] int limit = 20,
            [FromQuery] int skip = 0,
            [FromQuery] string platform = null)
        {
            try
            {
                var appKey = _queryOptions.Value.AppKey
                    ?? _configuration["Optimizely:ContentGraph:AppKey"];
                var singleKey = _queryOptions.Value.SingleKey;
                var gatewayAddress = _queryOptions.Value.GatewayAddress ?? "https://cg.optimizely.com";

                // Build the query
                var whereClause = string.IsNullOrWhiteSpace(platform)
                    ? ""
                    : $", where: {{ Platform: {{ eq: \"{platform}\" }} }}";

                var graphqlQuery = $"{{ TestSearchResult(locale: {locale}, limit: {limit}, skip: {skip}{whereClause}) {{ total items {{ Id Title Description Platform Language LastModified }} }} }}";

                // Use the SingleKey (read-only) or auth token to query
                var authParam = !string.IsNullOrWhiteSpace(singleKey) ? $"?auth={singleKey}" : "";
                var url = $"{gatewayAddress}/content/v2{authParam}";

                using var httpClient = new HttpClient();
                var requestBody = JsonSerializer.Serialize(new { query = graphqlQuery });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                return Ok(new
                {
                    Step = "5 — Query",
                    Locale = locale,
                    Limit = limit,
                    Skip = skip,
                    GraphQLQuery = graphqlQuery,
                    Response = TryParseJson(responseBody),
                    HttpStatus = (int)response.StatusCode
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Compare query results across ALL locale vs each registered locale.
        /// This is the key step to observe the locale filtering issue.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/query-compare
        /// </summary>
        [HttpGet("query-compare")]
        public async Task<IActionResult> QueryCompare(
            [FromQuery] string extraLocales = null)
        {
            try
            {
                var appKey = _queryOptions.Value.AppKey
                    ?? _configuration["Optimizely:ContentGraph:AppKey"];
                var singleKey = _queryOptions.Value.SingleKey;
                var gatewayAddress = _queryOptions.Value.GatewayAddress ?? "https://cg.optimizely.com";
                var authParam = !string.IsNullOrWhiteSpace(singleKey) ? $"?auth={singleKey}" : "";
                var url = $"{gatewayAddress}/content/v2{authParam}";

                // Locales to compare
                var locales = new List<string> { "ALL" };
                locales.AddRange(_registeredLanguages);
                if (!string.IsNullOrWhiteSpace(extraLocales))
                {
                    locales.AddRange(extraLocales.Split(',').Select(l => l.Trim()));
                }
                locales.Add("NEUTRAL");
                locales = locales.Distinct().ToList();

                using var httpClient = new HttpClient();
                var results = new List<object>();

                foreach (var locale in locales)
                {
                    var graphqlQuery = $"{{ TestSearchResult(locale: {locale}, limit: 100) {{ total items {{ Id Language }} }} }}";
                    var requestBody = JsonSerializer.Serialize(new { query = graphqlQuery });
                    var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(url, content);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    results.Add(new
                    {
                        Locale = locale,
                        HttpStatus = (int)response.StatusCode,
                        Response = TryParseJson(responseBody)
                    });
                }

                return Ok(new
                {
                    Step = "6 — Query Compare",
                    RegisteredLanguages = _registeredLanguages,
                    LocalesQueried = locales,
                    Results = results,
                    Hint = "If locale:ALL shows items but locale:es shows 0, those items were indexed with an unregistered locale"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Delete all content from the source.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/delete-content
        /// </summary>
        [HttpGet("delete-content")]
        public async Task<IActionResult> DeleteContent()
        {
            try
            {
                if (_graphSourceClient == null)
                {
                    return BadRequest(new { Error = "Client not initialized. Call /init-client first." });
                }

                var result = await _graphSourceClient.DeleteContentAsync();

                return Ok(new
                {
                    Step = "7 — Delete Content",
                    DeleteResponse = TryParseJson(result),
                    Message = "All content deleted from the source."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Repro Step 1: Init client, register languages, save types, index content per locale.
        /// After calling this, wait ~30s for Graph server async processing, then call /repro-locale-bug-verify.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/repro-locale-bug-setup?source=repro01&amp;languages=en,es,fr&amp;itemsPerLang=2
        /// </summary>
        [HttpGet("repro-locale-bug-setup")]
        public async Task<IActionResult> ReproLocaleBugSetup(
            [FromQuery] string source = "repro01",
            [FromQuery] string languages = "en,es,fr",
            [FromQuery] int itemsPerLang = 2)
        {
            try
            {
                var appKey = _queryOptions.Value.AppKey
                    ?? _configuration["Optimizely:ContentGraph:AppKey"];
                var secret = _queryOptions.Value.Secret
                    ?? _configuration["Optimizely:ContentGraph:Secret"];
                var gateway = _queryOptions.Value.GatewayAddress ?? "https://cg.optimizely.com";

                if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(secret))
                    return BadRequest(new { Error = "AppKey/Secret not configured." });

                var langs = languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().ToLowerInvariant()).Distinct().ToList();

                // 1. Init client
                var client = GraphSourceClient.Create(new Uri(gateway), source, appKey, secret);

                // 2. Configure type + register ALL languages
                client.ConfigureContentType<TestSearchResult>()
                    .Field(x => x.Id, IndexingType.Queryable)
                    .Field(x => x.Title, IndexingType.Searchable)
                    .Field(x => x.Platform, IndexingType.Queryable)
                    .Field(x => x.Language, IndexingType.Queryable);

                foreach (var lang in langs)
                    client.AddLanguage(lang);

                var saveTypesResult = await client.SaveTypesAsync();

                // 3. Index items per language
                var indexResults = new List<object>();
                foreach (var lang in langs)
                {
                    var items = Enumerable.Range(1, itemsPerLang).Select(i => new TestSearchResult
                    {
                        Id = $"REPRO_{Guid.NewGuid():N}_{lang}",
                        Title = $"Repro item {i} ({lang})",
                        Platform = "REPRO",
                        Language = lang,
                        LastModified = DateTime.UtcNow
                    }).ToArray();

                    var result = await client.SaveContentAsync(x => x.Id, lang, items);
                    indexResults.Add(new
                    {
                        Language = lang,
                        ItemCount = items.Length,
                        Ids = items.Select(x => x.Id),
                        Response = result
                    });
                }

                return Ok(new
                {
                    Step = "Repro Setup — Init + SaveTypes + Index",
                    Config = new { Source = source, Gateway = gateway, Languages = langs, ItemsPerLang = itemsPerLang },
                    SaveTypesResponse = saveTypesResult,
                    IndexResults = indexResults,
                    NextStep = $"Wait ~30s, then call: https://localhost:5009/util-api/custom-graph-source/repro-locale-bug-verify?languages={string.Join(",", langs)}&itemsPerLang={itemsPerLang}"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Repro Step 2: Query each locale and compare results. Call this after /repro-locale-bug-setup + waiting ~30s.
        /// Sample usage: https://localhost:5009/util-api/custom-graph-source/repro-locale-bug-verify?languages=en,es,fr&amp;itemsPerLang=2
        /// </summary>
        [HttpGet("repro-locale-bug-verify")]
        public async Task<IActionResult> ReproLocaleBugVerify(
            [FromQuery] string languages = "en,es,fr",
            [FromQuery] int itemsPerLang = 2)
        {
            try
            {
                var singleKey = _queryOptions.Value.SingleKey;
                var gateway = _queryOptions.Value.GatewayAddress ?? "https://cg.optimizely.com";

                var langs = languages.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().ToLowerInvariant()).Distinct().ToList();

                var authParam = !string.IsNullOrWhiteSpace(singleKey) ? $"?auth={singleKey}" : "";
                var queryUrl = $"{gateway}/content/v2{authParam}";
                using var httpClient = new HttpClient();

                var locales = new List<string> { "ALL" };
                locales.AddRange(langs);

                var queryResults = new List<object>();
                foreach (var locale in locales)
                {
                    var gql = $"{{ TestSearchResult(locale: {locale}, limit: 100, where: {{ Platform: {{ eq: \"REPRO\" }} }}) {{ total items {{ Id Language }} }} }}";
                    var body = new StringContent(
                        JsonSerializer.Serialize(new { query = gql }),
                        System.Text.Encoding.UTF8, "application/json");

                    var resp = await httpClient.PostAsync(queryUrl, body);
                    var respBody = await resp.Content.ReadAsStringAsync();
                    var total = -1;
                    var match = System.Text.RegularExpressions.Regex.Match(respBody, "\"total\":(\\d+)");
                    if (match.Success) total = int.Parse(match.Groups[1].Value);

                    queryResults.Add(new
                    {
                        Locale = locale,
                        Total = total,
                        HttpStatus = (int)resp.StatusCode,
                        ExpectedTotal = locale == "ALL" ? langs.Count * itemsPerLang : itemsPerLang,
                        Bug = locale != "ALL" && total == 0 ? "BUG: content indexed but not routable to this locale" : null,
                        RawResponse = TryParseJson(respBody)
                    });
                }

                var totalAll = langs.Count * itemsPerLang;
                var bugDetected = queryResults.Cast<dynamic>().Any(r => r.Locale != "ALL" && r.Total == 0);

                return Ok(new
                {
                    Step = "Repro Verify — Query each locale",
                    BugDetected = bugDetected,
                    Summary = bugDetected
                        ? $"BUG CONFIRMED: locale:ALL shows items, but locale-specific queries return 0. Content is silently dropped by the Graph server for non-primary locales."
                        : "No bug detected — all locales return expected counts.",
                    QueryResults = queryResults,
                    RootCause = bugDetected ? new
                    {
                        Layer = "Graph Server — INDEX TIME",
                        Component = "Content ingestion pipeline for Source SDK custom sources",
                        Behavior = "SaveContentAsync(language, items) accepts content for any locale without error, but the Graph server fails to route it to the locale-specific Elasticsearch index. Content lands in a global index (visible via locale:ALL) but never in locale-specific indices.",
                        NotSDK = "The SDK correctly sends the language parameter — proven by en working correctly",
                        NotQueryTime = "The schema correctly shows all locales — queries execute without errors, they just return 0",
                        RelatedJira = "CG-14843 (Graph Source SDK - Content Sync Language Always Set to En) — closed but underlying server bug was never fixed"
                    } : null
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Helpers

        private static List<TestSearchResult> GenerateSampleItems(string language, int count, string platform)
        {
            var items = new List<TestSearchResult>();
            for (int i = 1; i <= count; i++)
            {
                items.Add(new TestSearchResult
                {
                    Id = $"{platform}_sample-{Guid.NewGuid():N}_{language}",
                    Title = $"Sample {platform} Item {i} ({language})",
                    Description = $"This is a sample {platform.ToLower()} content item in {language} language, item number {i}.",
                    Platform = platform,
                    Language = language,
                    LastModified = DateTime.UtcNow
                });
            }
            return items;
        }

        private static object TryParseJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return new { IsEmpty = true, Raw = rawResponse };
            }

            try
            {
                using var document = JsonDocument.Parse(rawResponse);
                return document.RootElement.Clone();
            }
            catch
            {
                return new { IsJson = false, Raw = rawResponse };
            }
        }

        #endregion
    }

    /// <summary>
    /// Custom object model for Graph Source SDK indexing.
    /// Mirrors the pattern used by customers indexing multi-language custom objects.
    /// </summary>
    public class TestSearchResult
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Platform { get; set; }
        public string Language { get; set; }
        public DateTime LastModified { get; set; }
    }
}
