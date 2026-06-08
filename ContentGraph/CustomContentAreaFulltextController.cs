using System.Text.Json;
using EPiServer.ContentApi.Core.Serialization;
using EPiServer.ContentApi.Core.Serialization.Models;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to diagnose why _fulltext on a page does not include content 
    /// from blocks inside a ContentArea. Each step isolates a different part of the
    /// serialization / indexing pipeline so the root cause can be identified.
    ///
    /// Common root causes:
    /// 1. Block properties decorated with [GraphProperty(PropertyIndexingMode.OutputOnly)] — excluded from _fulltext by design.
    /// 2. Custom IContentApiModelProperty placing "Content" at the end of ContentType list — ContentApiModelTransformer.cs uses .Last() to resolve type.
    /// 3. SearchableSuffixMaxDepth too low for deeply nested content.
    /// 4. ExpandLevel.Default too low — blocks in ContentArea are not expanded.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-content-area-fulltext")]
    public class CustomContentAreaFulltextController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IClient _graphClient;
        private readonly IContentConverterProvider _contentConverterProvider;

        public CustomContentAreaFulltextController(
            IContentLoader contentLoader,
            IContentTypeRepository contentTypeRepository,
            IOptions<QueryOptions> queryOptions,
            IClient graphClient,
            IContentConverterProvider contentConverterProvider)
        {
            _contentLoader = contentLoader;
            _contentTypeRepository = contentTypeRepository;
            _queryOptions = queryOptions;
            _graphClient = graphClient;
            _contentConverterProvider = contentConverterProvider;
        }

        /// <summary>
        /// Step 1: Show configuration and sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/config
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
                    Description = "Shows the current Content Graph configuration that affects ContentArea expansion and _fulltext population.",
                    ContentGraph = new
                    {
                        GatewayAddress = opts.GatewayAddress,
                        HasSingleKey = !string.IsNullOrWhiteSpace(opts.SingleKey),
                        IncludeInheritanceInContentType = opts.IncludeInheritanceInContentType,
                        PreventFieldCollision = opts.PreventFieldCollision,
                        SearchableSuffixMaxDepth = opts.SearchableSuffixMaxDepth,
                        ExpandLevel = new
                        {
                            Default = opts.ExpandLevel.Default,
                            ContentArea = opts.ExpandLevel.ContentArea,
                            ExpandPageContentAreasInContentAreas = opts.ExpandLevel.ExpandPageContentAreasInContentAreas
                        }
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-content-area-fulltext/config",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/inspect-page?contentId=6",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/inspect-block?contentId=100",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/inspect-content-area?contentId=6&propertyName=MainContentArea",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/check-graph-fulltext?typeName=FoundationPageData&searchTerm=hello",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/check-block-graph-fulltext?typeName=TextBlock&limit=3",
                        "https://localhost:5009/util-api/custom-content-area-fulltext/check-expanded-fulltext?typeName=FoundationPageData&contentAreaField=MainContentArea&contentId=6"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Inspect a page's content model — shows ContentType list, all properties with their
        /// indexing attributes ([Searchable], [GraphProperty]), and ContentArea properties with their items.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/inspect-page?contentId=6
        /// </summary>
        [HttpGet("inspect-page")]
        public IActionResult InspectPage([FromQuery] int contentId = 6)
        {
            try
            {
                if (!_contentLoader.TryGet<IContent>(new ContentReference(contentId), out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                var contentType = _contentTypeRepository.Load(content.ContentTypeID);
                var propertyInfos = GetPropertyInfos(content, contentType);
                var contentAreaProperties = GetContentAreaDetails(content);

                return Ok(new
                {
                    Step = "2 - Inspect Page",
                    Description = "Shows a page's ContentType list and all properties with their indexing attributes. Look for [GraphProperty(OutputOnly)] or missing [Searchable] on text properties.",
                    ContentId = contentId,
                    Name = content.Name,
                    ContentTypeName = contentType?.Name,
                    ContentTypeInheritance = GetContentTypeInheritanceChain(content),
                    Properties = propertyInfos,
                    ContentAreas = contentAreaProperties
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Inspect a block's content model — shows the block's ContentType list and all properties
        /// with their indexing attributes. This helps identify if OutputOnly is used.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/inspect-block?contentId=100
        /// </summary>
        [HttpGet("inspect-block")]
        public IActionResult InspectBlock([FromQuery] int contentId = 100)
        {
            try
            {
                if (!_contentLoader.TryGet<IContent>(new ContentReference(contentId), out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                var contentType = _contentTypeRepository.Load(content.ContentTypeID);
                var propertyInfos = GetPropertyInfos(content, contentType);

                // Check if the block has any searchable string/XhtmlString properties
                var searchableTextProps = propertyInfos
                    .Where(p => (p.ClrType == "XhtmlString" || p.ClrType == "String") && p.IsSearchable)
                    .ToList();

                var outputOnlyProps = propertyInfos
                    .Where(p => p.GraphPropertyMode == "OutputOnly" || p.GraphPropertyMode == "Default")
                    .ToList();

                return Ok(new
                {
                    Step = "3 - Inspect Block",
                    Description = "Shows a block's properties with their indexing attributes. Properties with [GraphProperty(OutputOnly)] are excluded from _fulltext.",
                    ContentId = contentId,
                    Name = content.Name,
                    ContentTypeName = contentType?.Name,
                    ContentTypeInheritance = GetContentTypeInheritanceChain(content),
                    Diagnosis = new
                    {
                        TotalProperties = propertyInfos.Count,
                        SearchableTextProperties = searchableTextProps.Count,
                        OutputOnlyProperties = outputOnlyProps.Count,
                        Verdict = outputOnlyProps.Any()
                            ? $"WARNING: {outputOnlyProps.Count} properties have [GraphProperty] attribute (OutputOnly or Default) and will NOT appear in _fulltext. Remove [GraphProperty] entirely and use [Searchable(true)] instead."
                            : searchableTextProps.Any()
                                ? "OK: Block has searchable text properties — these should appear in _fulltext."
                                : "INFO: Block has no string/XhtmlString properties marked as searchable."
                    },
                    Properties = propertyInfos
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Inspect a ContentArea property — shows all items in the area with their types,
        /// property attributes, and whether they would contribute to parent's _fulltext.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/inspect-content-area?contentId=6&amp;propertyName=MainContentArea
        /// </summary>
        [HttpGet("inspect-content-area")]
        public IActionResult InspectContentArea(
            [FromQuery] int contentId = 6,
            [FromQuery] string propertyName = "MainContentArea")
        {
            try
            {
                if (!_contentLoader.TryGet<IContent>(new ContentReference(contentId), out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                var property = (content as IContentData)?.Property[propertyName];
                if (property == null)
                {
                    return NotFound(new { Error = $"Property '{propertyName}' not found on content ID {contentId}." });
                }

                var contentArea = property.Value as ContentArea;
                if (contentArea == null || contentArea.Items == null || !contentArea.Items.Any())
                {
                    return Ok(new
                    {
                        Step = "4 - Inspect ContentArea",
                        ContentId = contentId,
                        PropertyName = propertyName,
                        Items = Array.Empty<object>(),
                        Diagnosis = "ContentArea is empty or null."
                    });
                }

                var items = new List<object>();
                foreach (var item in contentArea.Items)
                {
                    if (!_contentLoader.TryGet<IContent>(item.ContentLink, out var blockContent))
                    {
                        items.Add(new
                        {
                            ContentLinkId = item.ContentLink?.ID,
                            Status = "NOT_FOUND",
                            Error = "Block could not be loaded — may be deleted, draft, or permission-restricted."
                        });
                        continue;
                    }

                    var blockType = _contentTypeRepository.Load(blockContent.ContentTypeID);
                    var blockProps = GetPropertyInfos(blockContent, blockType);

                    var searchableTextProps = blockProps
                        .Where(p => (p.ClrType == "XhtmlString" || p.ClrType == "String") && p.IsSearchable)
                        .ToList();

                    var outputOnlyProps = blockProps
                        .Where(p => p.GraphPropertyMode == "OutputOnly" || p.GraphPropertyMode == "Default")
                        .ToList();

                    items.Add(new
                    {
                        ContentLinkId = blockContent.ContentLink.ID,
                        Name = blockContent.Name,
                        ContentTypeName = blockType?.Name,
                        ContentTypeChain = GetContentTypeInheritanceChain(blockContent),
                        SearchableTextPropertyCount = searchableTextProps.Count,
                        OutputOnlyPropertyCount = outputOnlyProps.Count,
                        WillContributeToFulltext = searchableTextProps.Any(),
                        OutputOnlyProperties = outputOnlyProps.Select(p => p.Name).ToList(),
                        SearchableTextProperties = searchableTextProps.Select(p => new { p.Name, p.ClrType }).ToList()
                    });
                }

                var blocksWithFulltext = items.Cast<dynamic>().Count(i => i.WillContributeToFulltext == true);
                var blocksWithOutputOnly = items.Cast<dynamic>().Count(i => i.OutputOnlyPropertyCount > 0);

                return Ok(new
                {
                    Step = "4 - Inspect ContentArea",
                    Description = "Shows each block in the ContentArea and whether its properties will contribute to _fulltext.",
                    ContentId = contentId,
                    PropertyName = propertyName,
                    TotalBlocks = items.Count,
                    BlocksContributingToFulltext = blocksWithFulltext,
                    BlocksWithOutputOnly = blocksWithOutputOnly,
                    Diagnosis = blocksWithOutputOnly > 0
                        ? $"WARNING: {blocksWithOutputOnly} block(s) have OutputOnly properties that will NOT appear in _fulltext."
                        : blocksWithFulltext > 0
                            ? "OK: All blocks with text properties will contribute to _fulltext."
                            : "INFO: No blocks have searchable text properties.",
                    Items = items
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Query Content Graph to check _fulltext for a page type.
        /// Sends a raw GraphQL query to verify what appears in the indexed _fulltext.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/check-graph-fulltext?typeName=FoundationPageData&amp;searchTerm=hello
        /// </summary>
        [HttpGet("check-graph-fulltext")]
        public async Task<IActionResult> CheckGraphFulltext(
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string searchTerm = "",
            [FromQuery] int contentId = 0,
            [FromQuery] int limit = 3)
        {
            try
            {
                var whereClause = "";
                if (contentId > 0)
                {
                    whereClause = $"where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}";
                }
                else if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    whereClause = $"where: {{ _fulltext: {{ match: \"{EscapeGraphQl(searchTerm)}\" }} }}";
                }

                var query = $@"{{
  {typeName}(limit: {limit}{(string.IsNullOrEmpty(whereClause) ? "" : $", {whereClause}")}) {{
    items {{
      Name
      _fulltext
      ContentLink {{ Id }}
      ContentType
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "5 - Check Graph _fulltext",
                    Description = "Queries Content Graph to see what is in _fulltext for a page type. If _fulltext only contains Name values (no actual body/heading content), the block properties are likely OutputOnly.",
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse)
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Query Content Graph to check _fulltext for a block type directly.
        /// Helps verify whether the block's own _fulltext contains its Heading/BodyText.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/check-block-graph-fulltext?typeName=TextBlock&amp;limit=3
        /// </summary>
        [HttpGet("check-block-graph-fulltext")]
        public async Task<IActionResult> CheckBlockGraphFulltext(
            [FromQuery] string typeName = "TextBlock",
            [FromQuery] int contentId = 0,
            [FromQuery] int limit = 3)
        {
            try
            {
                var whereClause = contentId > 0
                    ? $", where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}"
                    : "";

                var query = $@"{{
  {typeName}(limit: {limit}{whereClause}) {{
    items {{
      Name
      _fulltext
      ContentLink {{ Id }}
      ContentType
    }}
    total
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "6 - Check Block _fulltext",
                    Description = "Queries Content Graph for a block type's own _fulltext. If _fulltext only contains the Name (not Heading, BodyText, etc.), the block's properties are configured as OutputOnly or not Searchable.",
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse)
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Query Content Graph to check the Expanded._fulltext of blocks inside a ContentArea.
        /// This verifies whether the Graph crawler is correctly expanding and serializing the blocks.
        /// Sample usage: https://localhost:5009/util-api/custom-content-area-fulltext/check-expanded-fulltext?typeName=FoundationPageData&amp;contentAreaField=MainContentArea&amp;contentId=6
        /// </summary>
        [HttpGet("check-expanded-fulltext")]
        public async Task<IActionResult> CheckExpandedFulltext(
            [FromQuery] string typeName = "FoundationPageData",
            [FromQuery] string contentAreaField = "MainContentArea",
            [FromQuery] int contentId = 0,
            [FromQuery] int limit = 1)
        {
            try
            {
                var whereClause = contentId > 0
                    ? $", where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}"
                    : "";

                var query = $@"{{
  {typeName}(limit: {limit}{whereClause}) {{
    items {{
      Name
      _fulltext
      ContentLink {{ Id }}
      {contentAreaField} {{
        ContentLink {{
          Id
          Expanded {{
            Name
            _fulltext
            ContentType
          }}
        }}
      }}
    }}
  }}
}}";

                var rawResponse = await _graphClient.QueryAsync(query, new { });

                return Ok(new
                {
                    Step = "7 - Check Expanded _fulltext",
                    Description = "Queries Content Graph for a page's ContentArea → Expanded → _fulltext. If Expanded._fulltext is empty but the block's own _fulltext has content, the issue is in ContentArea expansion. If the block's own _fulltext is also empty, the issue is in the block's property configuration (OutputOnly).",
                    GraphQLQuery = query,
                    Response = TryParseJson(rawResponse)
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Private Helpers

        private List<PropertyInfo> GetPropertyInfos(IContent content, EPiServer.DataAbstraction.ContentType contentType)
        {
            var result = new List<PropertyInfo>();
            // Use ModelType from ContentType to avoid proxy class issues.
            // content.GetType() returns e.g. FulltextTestBlockOutputOnlyProxy which doesn't carry attributes.
            var modelType = contentType?.ModelType ?? content.GetType();

            foreach (var propDef in contentType?.PropertyDefinitions ?? Enumerable.Empty<PropertyDefinition>())
            {
                var clrProp = modelType.GetProperty(propDef.Name);
                var propType = clrProp?.PropertyType;

                // Check for [Searchable] attribute (inherit=true to check base classes)
                var searchableAttr = clrProp?.GetCustomAttributes(typeof(SearchableAttribute), true)
                    .OfType<SearchableAttribute>().FirstOrDefault();

                // Check for [GraphProperty] attribute by name match (inherit=true)
                var graphPropAttr = clrProp?.GetCustomAttributes(true)
                    .FirstOrDefault(a => a.GetType().Name == "GraphPropertyAttribute");

                string graphPropertyMode = null;
                if (graphPropAttr != null)
                {
                    var modeProp = graphPropAttr.GetType().GetProperty("PropertyIndexingMode")
                        ?? graphPropAttr.GetType().GetProperty("Mode");
                    if (modeProp != null)
                    {
                        graphPropertyMode = modeProp.GetValue(graphPropAttr)?.ToString();
                    }
                }

                // Determine effective searchability:
                // - If [GraphProperty] is set to OutputOnly or Default → NOT searchable (per PropertyUtil.cs lines 68-72)
                // - Otherwise, use [Searchable] attribute or default (string/XhtmlString default to searchable)
                var hasGraphPropertyAttr = graphPropertyMode != null;
                bool isSearchable;
                if (hasGraphPropertyAttr)
                {
                    // GraphProperty overrides everything — both OutputOnly and Default return false
                    isSearchable = false;
                }
                else
                {
                    isSearchable = searchableAttr?.IsSearchable ?? (propType == typeof(string) || propType == typeof(XhtmlString));
                }

                result.Add(new PropertyInfo
                {
                    Name = propDef.Name,
                    ClrType = propType?.Name ?? propDef.Type?.DataType.ToString() ?? "Unknown",
                    IsSearchable = isSearchable,
                    SearchableExplicit = searchableAttr != null ? searchableAttr.IsSearchable : null,
                    GraphPropertyMode = graphPropertyMode,
                    HasValue = (content as IContentData)?.Property[propDef.Name]?.Value != null,
                    ValuePreview = GetValuePreview((content as IContentData)?.Property[propDef.Name]?.Value)
                });
            }

            return result;
        }

        private List<string> GetContentTypeInheritanceChain(IContent content)
        {
            var chain = new List<string>();
            var type = content.GetType();
            while (type != null && type != typeof(object))
            {
                chain.Add(type.Name);
                type = type.BaseType;
            }
            return chain;
        }

        private List<object> GetContentAreaDetails(IContent content)
        {
            var result = new List<object>();
            var contentData = content as IContentData;
            if (contentData == null) return result;

            foreach (var prop in contentData.Property)
            {
                if (prop.Value is ContentArea contentArea && contentArea.Items != null)
                {
                    result.Add(new
                    {
                        PropertyName = prop.Name,
                        ItemCount = contentArea.Items.Count,
                        Items = contentArea.Items.Select(item =>
                        {
                            _contentLoader.TryGet<IContent>(item.ContentLink, out var blockContent);
                            return new
                            {
                                ContentLinkId = item.ContentLink?.ID,
                                Name = blockContent?.Name ?? "NOT_FOUND",
                                ContentTypeName = blockContent != null
                                    ? _contentTypeRepository.Load(blockContent.ContentTypeID)?.Name
                                    : null
                            };
                        }).ToList()
                    });
                }
            }

            return result;
        }

        private static string GetValuePreview(object value)
        {
            if (value == null) return null;
            var str = value.ToString();
            if (str.Length > 120)
            {
                str = str.Substring(0, 120) + "...";
            }
            return str;
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

        private static string EscapeGraphQl(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        #endregion

        #region DTOs

        private class PropertyInfo
        {
            public string Name { get; set; }
            public string ClrType { get; set; }
            public bool IsSearchable { get; set; }
            public bool? SearchableExplicit { get; set; }
            public string GraphPropertyMode { get; set; }
            public bool HasValue { get; set; }
            public string ValuePreview { get; set; }
        }

        #endregion
    }
}
