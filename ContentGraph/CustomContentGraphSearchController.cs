using System.Globalization;
using System.Text.Json;
using EPiServer.Globalization;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Core;
using Optimizely.Graph.QueryBuilder;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller with small Content Graph search steps for comparing cached and uncached query results.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-content-graph-search")]
    public class CustomContentGraphSearchController : ControllerBase
    {
        private readonly IClient _contentGraphClient;
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly ISiteDefinitionRepository _siteDefinitionRepository;

        public CustomContentGraphSearchController(
            IClient contentGraphClient,
            IOptions<QueryOptions> queryOptions,
            ISiteDefinitionRepository siteDefinitionRepository)
        {
            _contentGraphClient = contentGraphClient;
            _queryOptions = queryOptions;
            _siteDefinitionRepository = siteDefinitionRepository;
        }

        /// <summary>
        /// Step 1: Shows the local defaults used by the sample query.
        /// Sample usage: https://localhost:5009/util-api/custom-content-graph-search/defaults
        /// </summary>
        [HttpGet("defaults")]
        public IActionResult Defaults()
        {
            try
            {
                var currentSite = SiteDefinition.Current;
                var configuredSites = _siteDefinitionRepository.List()
                    .Select(x => new
                    {
                        x.Name,
                        x.Id,
                        SiteUrl = x.SiteUrl?.ToString(),
                        StartPage = x.StartPage?.ToString()
                    })
                    .ToList();

                return Ok(new
                {
                    CurrentSite = new
                    {
                        currentSite?.Name,
                        currentSite?.Id,
                        SiteUrl = currentSite?.SiteUrl?.ToString(),
                        StartPage = currentSite?.StartPage?.ToString()
                    },
                    CurrentLocale = NormalizeLocale(ContentLanguage.PreferredCulture),
                    ContentGraph = new
                    {
                        GatewayAddress = _queryOptions.Value.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.AppKey),
                        HasSingleKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.SingleKey),
                        HasSecret = !string.IsNullOrWhiteSpace(_queryOptions.Value.Secret)
                    },
                    ConfiguredSites = configuredSites,
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-content-graph-search/build-query?searchTerm=blur&typeName=FoundationPageData",
                        "https://localhost:5009/util-api/custom-content-graph-search/execute-cached?searchTerm=blur&typeName=FoundationPageData",
                        "https://localhost:5009/util-api/custom-content-graph-search/execute-uncached?searchTerm=blur&typeName=FoundationPageData",
                        "https://localhost:5009/util-api/custom-content-graph-search/compare-cache?searchTerm=blur&typeName=FoundationPageData"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Builds the SDK-like GraphQL query without sending it.
        /// Sample usage: https://localhost:5009/util-api/custom-content-graph-search/build-query?searchTerm=blur&amp;typeName=FoundationPageData
        /// </summary>
        [HttpGet("build-query")]
        public IActionResult BuildQuery(
            [FromQuery] string searchTerm = "blur",
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string locale = null,
            [FromQuery] string siteId = null,
            [FromQuery] int skip = 0,
            [FromQuery] int limit = 10,
            [FromQuery] bool includeSiteFilter = true)
        {
            try
            {
                var request = BuildSdkLikeQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter);

                return Ok(new
                {
                    Step = "Build query",
                    request.OperationName,
                    request.Query,
                    Parameters = new
                    {
                        SearchTerm = searchTerm,
                        TypeName = typeName,
                        Locale = ResolveLocale(locale),
                        SiteId = ResolveSiteId(siteId),
                        Skip = skip,
                        Limit = limit,
                        IncludeSiteFilter = includeSiteFilter
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Executes the SDK-like query with cache enabled.
        /// Sample usage: https://localhost:5009/util-api/custom-content-graph-search/execute-cached?searchTerm=blur&amp;typeName=FoundationPageData
        /// </summary>
        [HttpGet("execute-cached")]
        public async Task<IActionResult> ExecuteCached(
            [FromQuery] string searchTerm = "blur",
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string locale = null,
            [FromQuery] string siteId = null,
            [FromQuery] int skip = 0,
            [FromQuery] int limit = 10,
            [FromQuery] bool includeSiteFilter = true)
        {
            try
            {
                var result = await ExecuteQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter, useCache: true);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Executes the same SDK-like query with cache disabled.
        /// Sample usage: https://localhost:5009/util-api/custom-content-graph-search/execute-uncached?searchTerm=blur&amp;typeName=FoundationPageData
        /// </summary>
        [HttpGet("execute-uncached")]
        public async Task<IActionResult> ExecuteUncached(
            [FromQuery] string searchTerm = "blur",
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string locale = null,
            [FromQuery] string siteId = null,
            [FromQuery] int skip = 0,
            [FromQuery] int limit = 10,
            [FromQuery] bool includeSiteFilter = true)
        {
            try
            {
                var result = await ExecuteQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter, useCache: false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Executes the same SDK-like query twice and returns cached and uncached responses together.
        /// Sample usage: https://localhost:5009/util-api/custom-content-graph-search/compare-cache?searchTerm=blur&amp;typeName=FoundationPageData
        /// </summary>
        [HttpGet("compare-cache")]
        public async Task<IActionResult> CompareCache(
            [FromQuery] string searchTerm = "blur",
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string locale = null,
            [FromQuery] string siteId = null,
            [FromQuery] int skip = 0,
            [FromQuery] int limit = 10,
            [FromQuery] bool includeSiteFilter = true)
        {
            try
            {
                var cached = await ExecuteQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter, useCache: true);
                var uncached = await ExecuteQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter, useCache: false);

                return Ok(new
                {
                    Step = "Compare cache",
                    QueryIsSame = cached.Query == uncached.Query,
                    Cached = cached,
                    Uncached = uncached
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private async Task<QueryExecutionResult> ExecuteQuery(
            string searchTerm,
            string typeName,
            string locale,
            string siteId,
            int skip,
            int limit,
            bool includeSiteFilter,
            bool useCache)
        {
            var request = BuildSdkLikeQuery(searchTerm, typeName, locale, siteId, skip, limit, includeSiteFilter);
            var queryStringParams = new Dictionary<string, string>
            {
                { "cache", useCache.ToString().ToLowerInvariant() }
            };

            var rawResponse = await _contentGraphClient.QueryAsync(
                request.Query,
                new { },
                customHeaders: null,
                queryStringParams: queryStringParams);

            return new QueryExecutionResult
            {
                Step = useCache ? "Execute cached" : "Execute uncached",
                RequestPath = $"content/v2?cache={useCache.ToString().ToLowerInvariant()}",
                OperationName = request.OperationName,
                Query = request.Query,
                Parameters = new
                {
                    SearchTerm = searchTerm,
                    TypeName = typeName,
                    Locale = ResolveLocale(locale),
                    SiteId = ResolveSiteId(siteId),
                    Skip = skip,
                    Limit = limit,
                    IncludeSiteFilter = includeSiteFilter
                },
                RawResponse = TryParseJson(rawResponse)
            };
        }

        private GraphQLRequest BuildSdkLikeQuery(
            string searchTerm,
            string typeName,
            string locale,
            string siteId,
            int skip,
            int limit,
            bool includeSiteFilter)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException("typeName is required.", nameof(typeName));
            }

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                throw new ArgumentException("searchTerm is required.", nameof(searchTerm));
            }

            var normalizedLocale = ResolveLocale(locale);
            var resolvedSiteId = ResolveSiteId(siteId);

            const string operationName = "Custom_ContentGraph_Search_Query";
            var typeQuery = new GraphQueryBuilder()
                .ForType(typeName)
                .Search(EscapeGraphQlString(searchTerm))
                .Skip(skip)
                .Limit(limit)
                .OrderBy(Ranking.SEMANTIC)
                .Locales(new[] { normalizedLocale })
                .Fields(
                    "Name",
                    "ContentLink.Id",
                    "Language.Name",
                    "ContentType",
                    "SiteId")
                .Where(FilterExpression.Group(
                    "_not",
                    FilterExpression.Field("DisableIndexing", new StringFilterOperators().Eq("true", addDoubleQuote: false))))
                .Where("Status", new StringFilterOperators().Eq("Published"));

            if (includeSiteFilter)
            {
                typeQuery.Where("SiteId", new StringFilterOperators().Eq(EscapeGraphQlString(resolvedSiteId)));
            }

            typeQuery.Total();

            return new GraphQLRequest
            {
                OperationName = operationName,
                Query = $"query {operationName} {{{typeQuery.ToTypeQuery()}}}"
            };
        }

        private string ResolveSiteId(string siteId)
        {
            if (!string.IsNullOrWhiteSpace(siteId))
            {
                return siteId;
            }

            var currentSite = SiteDefinition.Current;
            if (currentSite != null && currentSite.Id != Guid.Empty)
            {
                return currentSite.Id.ToString();
            }

            return _siteDefinitionRepository.List().FirstOrDefault()?.Id.ToString() ?? string.Empty;
        }

        private static string ResolveLocale(string locale)
        {
            return string.IsNullOrWhiteSpace(locale)
                ? NormalizeLocale(ContentLanguage.PreferredCulture)
                : NormalizeLocale(locale);
        }

        private static string NormalizeLocale(CultureInfo culture)
        {
            return NormalizeLocale(culture?.Name);
        }

        private static string NormalizeLocale(string locale)
        {
            return string.IsNullOrWhiteSpace(locale)
                ? "en"
                : locale.Replace("-", "_");
        }

        private static object TryParseJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return new
                {
                    IsEmpty = true,
                    Raw = rawResponse
                };
            }

            try
            {
                using var document = JsonDocument.Parse(rawResponse);
                return document.RootElement.Clone();
            }
            catch
            {
                return new
                {
                    IsJson = false,
                    Raw = rawResponse
                };
            }
        }

        private static string EscapeGraphQlString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        private class QueryExecutionResult
        {
            public string Step { get; set; }

            public string RequestPath { get; set; }

            public string OperationName { get; set; }

            public string Query { get; set; }

            public object Parameters { get; set; }

            public object RawResponse { get; set; }
        }
    }
}
