using System.Text.Json;
using EPiServer.ContentApi.Core.Serialization;
using EPiServer.ContentApi.Core.Serialization.Models;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to reproduce and investigate the Content Graph base type inheritance
    /// querying issue. When migrating from Search &amp; Navigation, querying by a base/parent
    /// page type (e.g. ForType&lt;BasePage&gt;()) does not return derived types.
    ///
    /// Each step isolates a different part of the problem:
    /// 1. Current IncludeInheritanceInContentType configuration
    /// 2. CMS-side content type hierarchy inspection
    /// 3. What ContentType field is actually indexed in Graph
    /// 4. Querying the concrete type root (the S&amp;N-like approach that fails)
    /// 5. Querying with ContentType filter (the correct Graph approach)
    /// 6. Side-by-side comparison of both approaches
    /// </summary>
    [ApiController]
    [Route("util-api/custom-content-type-inheritance")]
    public class CustomContentTypeInheritanceController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IClient _graphClient;

        public CustomContentTypeInheritanceController(
            IContentLoader contentLoader,
            IContentTypeRepository contentTypeRepository,
            IOptions<QueryOptions> queryOptions,
            IClient graphClient)
        {
            _contentLoader = contentLoader;
            _contentTypeRepository = contentTypeRepository;
            _queryOptions = queryOptions;
            _graphClient = graphClient;
        }

        /// <summary>
        /// Step 1: Shows current Content Graph configuration relevant to inheritance querying,
        /// and all sample URLs for this controller.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/config
        /// </summary>
        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            try
            {
                var opts = _queryOptions.Value;
                return Ok(new
                {
                    Step = "1 - Configuration",
                    Description = "Shows the current Content Graph config that controls whether base types are included in the indexed ContentType field. " +
                                  "When IncludeInheritanceInContentType is false, querying by a base type will not return derived content.",
                    ContentGraph = new
                    {
                        GatewayAddress = opts.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(opts.AppKey),
                        HasSingleKey = !string.IsNullOrWhiteSpace(opts.SingleKey),
                        IncludeInheritanceInContentType = opts.IncludeInheritanceInContentType,
                        PreventFieldCollision = opts.PreventFieldCollision,
                        SynchronizationEnabled = opts.SynchronizationEnabled
                    },
                    Explanation = new
                    {
                        WhenFalse = "ContentType field only contains [\"Page\", \"StandardPage\"]. Querying by base type (e.g. FoundationPageData) returns nothing.",
                        WhenTrue = "ContentType field includes full hierarchy: [\"FoundationPageData\", \"PageData\", \"ContentData\", \"Page\", \"StandardPage\"]. " +
                                   "Querying Content root with ContentType filter for FoundationPageData returns all derived types."
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-content-type-inheritance/config",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/inspect-hierarchy?contentId=98",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/check-indexed-content-type?contentId=98",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/query-concrete-type?typeName=StandardPage&limit=3",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/query-base-type-direct?baseTypeName=FoundationPageData&limit=3",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/query-base-type-via-filter?baseTypeName=FoundationPageData&limit=5",
                        "https://localhost:5009/util-api/custom-content-type-inheritance/compare-approaches?baseTypeName=FoundationPageData&limit=5"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Inspects the CMS-side C# type hierarchy for a given content item.
        /// Shows the full inheritance chain and what would be included in ContentType when
        /// IncludeInheritanceInContentType is true.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/inspect-hierarchy?contentId=98
        /// </summary>
        [HttpGet("inspect-hierarchy")]
        public IActionResult InspectHierarchy([FromQuery] int contentId = 98)
        {
            try
            {
                if (!_contentLoader.TryGet<IContent>(new ContentReference(contentId), out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                var contentType = _contentTypeRepository.Load(content.ContentTypeID);
                var modelType = contentType?.ModelType ?? content.GetType();

                // Walk full inheritance chain
                var fullChain = new List<string>();
                var type = modelType;
                while (type != null && type != typeof(object))
                {
                    fullChain.Add(type.Name);
                    type = type.BaseType;
                }

                // Walk filtered chain (what CustomContentTypeFilter actually adds)
                var filteredChain = new List<string>();
                type = modelType.BaseType; // skip the concrete type itself
                while (type != null && type != typeof(object))
                {
                    if (type != typeof(IContent))
                    {
                        filteredChain.Add(type.Name);
                    }
                    type = type.BaseType;
                }

                // Get interfaces
                var interfaces = modelType.GetInterfaces()
                    .Select(i => i.Name)
                    .ToList();

                return Ok(new
                {
                    Step = "2 - Inspect C# Type Hierarchy",
                    Description = "Shows the full C# inheritance chain for a content item. " +
                                  "When IncludeInheritanceInContentType=true, the 'BaseTypesAddedToContentType' list is appended to the indexed ContentType field.",
                    ContentId = contentId,
                    Name = content.Name,
                    ConcreteType = modelType.Name,
                    ContentTypeName = contentType?.Name,
                    FullInheritanceChain = fullChain,
                    BaseTypesAddedToContentType = filteredChain,
                    Interfaces = interfaces,
                    CurrentConfig = new
                    {
                        IncludeInheritanceInContentType = _queryOptions.Value.IncludeInheritanceInContentType
                    },
                    Explanation = _queryOptions.Value.IncludeInheritanceInContentType
                        ? "IncludeInheritanceInContentType is TRUE. After re-indexing, the ContentType field should include all items from BaseTypesAddedToContentType."
                        : "WARNING: IncludeInheritanceInContentType is FALSE. The ContentType field will NOT include base types. " +
                          "Enable it in Startup.cs: services.AddContentGraph(x => { x.IncludeInheritanceInContentType = true; }); then re-run the Graph sync job."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Queries Content Graph to show what is actually stored in the ContentType field
        /// for a specific content item. This reveals whether base types are indexed or not.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/check-indexed-content-type?contentId=98
        /// </summary>
        [HttpGet("check-indexed-content-type")]
        public async Task<IActionResult> CheckIndexedContentType([FromQuery] int contentId = 98)
        {
            try
            {
                // Use Content root to query by ContentLink.Id so we can see any content type
                var query = $@"{{
  Content(where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
      Language {{ Name }}
      Status
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "3 - Check Indexed ContentType Field",
                    Description = "Shows the actual ContentType array stored in Graph for a content item. " +
                                  "If base types (e.g. FoundationPageData, PageData) are NOT present, " +
                                  "IncludeInheritanceInContentType was false during indexing and a re-index is needed.",
                    ContentId = contentId,
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse),
                    Hint = "Compare the ContentType array here with the BaseTypesAddedToContentType from Step 2. " +
                           "If the base types are missing, enable IncludeInheritanceInContentType and re-run the sync job."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Queries Content Graph using the concrete type as the GraphQL root —
        /// this is the approach that works for concrete types but fails for base types.
        /// Equivalent to ForType&lt;StandardPage&gt;() in the .NET SDK.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/query-concrete-type?typeName=StandardPage&amp;limit=3
        /// </summary>
        [HttpGet("query-concrete-type")]
        public async Task<IActionResult> QueryConcreteType(
            [FromQuery] string typeName = "StandardPage",
            [FromQuery] int limit = 3)
        {
            try
            {
                var query = $@"{{
  {typeName}(limit: {limit}) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "4 - Query Concrete Type Root",
                    Description = $"Queries the '{typeName}' GraphQL root directly. This works for concrete types " +
                                  "registered as [ContentType]. This is equivalent to ForType<T>() in the .NET SDK.",
                    TypeName = typeName,
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse),
                    Note = "This approach only returns content whose concrete type IS the queried type. " +
                           "It does NOT return derived types — e.g. querying 'FoundationPageData' will NOT return StandardPage items."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Attempts to query Content Graph using the base type name directly as the
        /// GraphQL root. This demonstrates the FAILING approach — querying by a base class
        /// name that is NOT a registered concrete [ContentType] returns 0 results or an error.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/query-base-type-direct?baseTypeName=FoundationPageData&amp;limit=3
        /// </summary>
        [HttpGet("query-base-type-direct")]
        public async Task<IActionResult> QueryBaseTypeDirect(
            [FromQuery] string baseTypeName = "FoundationPageData",
            [FromQuery] int limit = 3)
        {
            try
            {
                // This query will likely fail or return 0 results because
                // FoundationPageData is abstract and not a Graph root type
                var query = $@"{{
  {baseTypeName}(limit: {limit}) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "5 - Query Base Type Directly (FAILING approach)",
                    Description = $"Attempts to query '{baseTypeName}' as a GraphQL root. " +
                                  "This is equivalent to ForType<FoundationPageData>() and typically FAILS " +
                                  "because abstract/base classes are not exposed as GraphQL root types in CMS 12.",
                    BaseTypeName = baseTypeName,
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse),
                    Explanation = "This is the Search & Navigation approach — it does NOT work in Content Graph. " +
                                  "In S&N, .Search<BaseType>() is polymorphic. In Graph, ForType<T>() just maps typeof(T).Name to a GraphQL root.",
                    Solution = "Use Step 6 instead: query the 'Content' root and filter by ContentType field."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Queries Content Graph using the Content root with a ContentType filter —
        /// this is the CORRECT approach to query all content inheriting from a base type.
        /// Requires IncludeInheritanceInContentType=true and a re-index.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/query-base-type-via-filter?baseTypeName=FoundationPageData&amp;limit=5
        /// </summary>
        [HttpGet("query-base-type-via-filter")]
        public async Task<IActionResult> QueryBaseTypeViaFilter(
            [FromQuery] string baseTypeName = "FoundationPageData",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{
  Content(
    limit: {limit},
    where: {{ ContentType: {{ eq: ""{EscapeGraphQl(baseTypeName)}"" }} }}
  ) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
      Language {{ Name }}
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "6 - Query Base Type via ContentType Filter (CORRECT approach)",
                    Description = $"Queries the 'Content' root and filters by ContentType containing '{baseTypeName}'. " +
                                  "This returns ALL content whose ContentType array includes the base type name.",
                    BaseTypeName = baseTypeName,
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse),
                    Prerequisites = new
                    {
                        Step1 = "Enable: services.AddContentGraph(x => { x.IncludeInheritanceInContentType = true; });",
                        Step2 = "If base class is abstract, either: (a) remove 'abstract' and set [ContentType(AvailableInEditMode = false)], " +
                                "or (b) the filter still works as long as the concrete class name is used.",
                        Step3 = "Re-run the 'Optimizely Graph content synchronization job' from CMS Admin."
                    },
                    DotNetEquivalent = $@"var query = _graphQueryBuilder
    .ForType<Content>()                                           // Use Content root (or a DTO named Content)
    .Fields(x => x.Name, x => x.ContentType)
    .Where(x => x.ContentType, new StringFilterOperators().Contains(""{baseTypeName}""))
    .ToQuery()
    .BuildQueries();"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Side-by-side comparison of the failing (direct base type query) vs
        /// the correct (Content root + ContentType filter) approach. Clearly demonstrates
        /// the difference in results.
        /// Sample usage: https://localhost:5009/util-api/custom-content-type-inheritance/compare-approaches?baseTypeName=FoundationPageData&amp;limit=5
        /// </summary>
        [HttpGet("compare-approaches")]
        public async Task<IActionResult> CompareApproaches(
            [FromQuery] string baseTypeName = "FoundationPageData",
            [FromQuery] int limit = 5)
        {
            try
            {
                // Approach 1: Direct base type query (FAILING)
                var directQuery = $@"{{
  {baseTypeName}(limit: {limit}) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                // Approach 2: Content root + ContentType filter (CORRECT)
                var filterQuery = $@"{{
  Content(
    limit: {limit},
    where: {{ ContentType: {{ eq: ""{EscapeGraphQl(baseTypeName)}"" }} }}
  ) {{
    items {{
      Name
      ContentType
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                string directResponse, filterResponse;

                try
                {
                    directResponse = await _graphClient.QueryAsync(directQuery, new { });
                }
                catch (Exception ex)
                {
                    directResponse = JsonSerializer.Serialize(new { Error = ex.Message });
                }

                try
                {
                    filterResponse = await _graphClient.QueryAsync(filterQuery, new { });
                }
                catch (Exception ex)
                {
                    filterResponse = JsonSerializer.Serialize(new { Error = ex.Message });
                }

                var directParsed = TryParseJson(directResponse);
                var filterParsed = TryParseJson(filterResponse);

                return Ok(new
                {
                    Step = "7 - Side-by-Side Comparison",
                    Description = $"Compares two approaches for querying all content inheriting from '{baseTypeName}'.",
                    IncludeInheritanceInContentType = _queryOptions.Value.IncludeInheritanceInContentType,
                    DirectApproach = new
                    {
                        Label = "FAILING: Query base type as GraphQL root",
                        Explanation = $"Queries '{baseTypeName}(...)' directly — equivalent to ForType<{baseTypeName}>(). " +
                                      "This only works if the type is a registered concrete [ContentType] with its own GraphQL root.",
                        GraphQLQuery = directQuery,
                        Response = directParsed
                    },
                    FilterApproach = new
                    {
                        Label = "CORRECT: Query Content root + ContentType filter",
                        Explanation = $"Queries 'Content(where: {{ ContentType: {{ eq: \"{baseTypeName}\" }} }})'. " +
                                      "This returns all content items whose ContentType array includes the base type.",
                        GraphQLQuery = filterQuery,
                        Response = filterParsed
                    },
                    Conclusion = _queryOptions.Value.IncludeInheritanceInContentType
                        ? "IncludeInheritanceInContentType is enabled. If the filter approach still returns 0 results, " +
                          "you may need to re-run the Graph sync job to re-index content with the updated ContentType field."
                        : "WARNING: IncludeInheritanceInContentType is currently DISABLED. " +
                          "The filter approach will return 0 results because base type names are not in the ContentType field. " +
                          "Enable it and re-index."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Private Helpers

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

        private static string EscapeGraphQl(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        #endregion
    }
}
