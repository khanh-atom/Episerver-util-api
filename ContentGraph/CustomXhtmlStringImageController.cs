using System.Net.Http;
using System.Text;
using System.Text.Json;
using EPiServer.ContentApi.Core.Configuration;
using EPiServer.ContentApi.Core.Serialization;
using EPiServer.ContentApi.Core.Serialization.Models;
using EPiServer.SpecializedProperties;
using Foundation.Features.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to replicate and investigate XhtmlString image property behavior in Content Graph.
    /// When an image is embedded inside XhtmlString (TinyMCE), its metadata (Alt, Title, etc.)
    /// is NOT returned in Graph queries — only raw HTML. This controller demonstrates the difference
    /// between XhtmlString vs ContentReference/ContentArea for image properties.
    /// Base route: util-api/custom-xhtmlstring-image
    /// </summary>
    [ApiController]
    [Route("util-api/custom-xhtmlstring-image")]
    public class CustomXhtmlStringImageController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IContentRepository _contentRepository;
        private readonly IConfiguration _configuration;
        private readonly ContentApiOptions _contentApiOptions;

        public CustomXhtmlStringImageController(
            IContentLoader contentLoader,
            IContentTypeRepository contentTypeRepository,
            IContentRepository contentRepository,
            IConfiguration configuration,
            IOptions<ContentApiOptions> contentApiOptions)
        {
            _contentLoader = contentLoader;
            _contentTypeRepository = contentTypeRepository;
            _contentRepository = contentRepository;
            _configuration = configuration;
            _contentApiOptions = contentApiOptions.Value;
        }

        /// <summary>
        /// Step 0: Index page showing all available endpoints and current configuration.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                var graphConfig = new
                {
                    GatewayAddress = _configuration["Optimizely:ContentGraph:GatewayAddress"],
                    HasAppKey = !string.IsNullOrWhiteSpace(_configuration["Optimizely:ContentGraph:AppKey"]),
                    HasSingleKey = !string.IsNullOrWhiteSpace(_configuration["Optimizely:ContentGraph:SingleKey"]),
                    RichTextFormat = _contentApiOptions.RichTextFormat.ToString()
                };

                return Ok(new
                {
                    Description = "XhtmlString Image Properties — Content Graph Behavior Investigation",
                    Issue = "When an image is embedded in XhtmlString (TinyMCE), its metadata (Alt, Title, custom props) is NOT available in Graph queries. Only raw HTML <img> tag is returned.",
                    GraphConfig = graphConfig,
                    Steps = new[]
                    {
                        "Step 1: GET /find-pages — Find StandardPages that have XhtmlString with embedded images",
                        "Step 2: GET /find-images — Find ImageMediaData items with their properties (Alt, Title, etc.)",
                        "Step 3: GET /inspect-xhtmlstring?contentId=883 — Show raw XhtmlString fragments and how images are stored internally",
                        "Step 4: GET /compare-property-types?contentId=883 — Compare XhtmlString vs ContentReference vs ContentArea image data",
                        "Step 5: GET /query-graph-html?contentId=883 — Query Content Graph for XhtmlString Html output",
                        "Step 6: GET /setup-test-content — Create a test StandardPage with image in both XhtmlString and ContentReference",
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-xhtmlstring-image",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/find-pages",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/find-images",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/inspect-xhtmlstring?contentId=883",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/compare-property-types?contentId=883",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/query-graph-html?contentId=883",
                        "https://localhost:5009/util-api/custom-xhtmlstring-image/setup-test-content",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Find StandardPages that have XhtmlString (MainBody) content containing img tags.
        /// Useful to find real content IDs for subsequent steps.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/find-pages?maxResults=10
        /// </summary>
        [HttpGet("find-pages")]
        public IActionResult FindPages([FromQuery] int maxResults = 10)
        {
            try
            {
                var startPage = ContentReference.StartPage;
                var allPages = _contentLoader.GetDescendents(ContentReference.RootPage)
                    .Take(500)
                    .Select(r =>
                    {
                        _contentLoader.TryGet<IContent>(r, out var c);
                        return c;
                    })
                    .Where(c => c != null)
                    .ToList();

                var standardPages = allPages
                    .OfType<Features.StandardPage.StandardPage>()
                    .ToList();

                var pagesWithImages = standardPages
                    .Where(p => p.MainBody != null && p.MainBody.ToHtmlString().Contains("<img", StringComparison.OrdinalIgnoreCase))
                    .Take(maxResults)
                    .Select(p => new
                    {
                        ContentId = p.ContentLink.ID,
                        Name = p.Name,
                        HasMainBody = p.MainBody != null,
                        MainBodyContainsImg = p.MainBody?.ToHtmlString()?.Contains("<img", StringComparison.OrdinalIgnoreCase) ?? false,
                        HasPageImage = !ContentReference.IsNullOrEmpty(p.PageImage),
                        PageImageId = p.PageImage?.ID,
                        HasMainContentArea = p.MainContentArea?.FilteredItems?.Any() ?? false,
                        MainBodyPreview = p.MainBody?.ToHtmlString()?.Substring(0, Math.Min(300, p.MainBody.ToHtmlString().Length)) + "..."
                    })
                    .ToList();

                var pagesWithoutImages = standardPages
                    .Where(p => p.MainBody != null && !p.MainBody.ToHtmlString().Contains("<img", StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(p => new
                    {
                        ContentId = p.ContentLink.ID,
                        Name = p.Name,
                        HasPageImage = !ContentReference.IsNullOrEmpty(p.PageImage),
                    })
                    .ToList();

                return Ok(new
                {
                    Step = "1 — Find Pages",
                    TotalStandardPages = standardPages.Count,
                    PagesWithImgInXhtmlString = pagesWithImages,
                    PagesWithoutImgInXhtmlString = pagesWithoutImages,
                    NextStep = pagesWithImages.Any()
                        ? $"Use contentId={pagesWithImages.First().ContentId} in next steps: /inspect-xhtmlstring?contentId={pagesWithImages.First().ContentId}"
                        : "No pages found with images in XhtmlString. Use /setup-test-content to create one."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Find ImageMediaData items and show their properties (Alt, Title, Description, etc.).
        /// These properties are available when querying via ContentReference/ContentArea but NOT via XhtmlString.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/find-images?maxResults=5
        /// </summary>
        [HttpGet("find-images")]
        public IActionResult FindImages([FromQuery] int maxResults = 5)
        {
            try
            {
                var allContent = _contentLoader.GetDescendents(ContentReference.RootPage)
                    .Take(1000)
                    .Select(r =>
                    {
                        _contentLoader.TryGet<ImageMediaData>(r, out var img);
                        return img;
                    })
                    .Where(img => img != null)
                    .Take(maxResults)
                    .Select(img => new
                    {
                        ContentId = img.ContentLink.ID,
                        Name = img.Name,
                        AltText = img.AltText,
                        Title = img.Title,
                        Description = img.Description,
                        Caption = img.Caption,
                        Copyright = img.Copyright,
                        CreditsText = img.CreditsText,
                        PropertiesAvailable = new[] { "AltText", "Title", "Description", "Caption", "Copyright", "CreditsText" },
                        Note = "All these properties are available via ContentReference Expanded query but NOT inside XhtmlString Html"
                    })
                    .ToList();

                return Ok(new
                {
                    Step = "2 — Find Images",
                    TotalFound = allContent.Count,
                    Images = allContent,
                    Explanation = "These image properties (AltText, Title, etc.) are queryable via Graph ONLY when the image is referenced as ContentReference or ContentArea — NOT when embedded in XhtmlString."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Inspect XhtmlString fragments of a page to show how images are stored internally.
        /// Demonstrates that images in XhtmlString are raw HTML img tags, not content references.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/inspect-xhtmlstring?contentId=883
        /// </summary>
        [HttpGet("inspect-xhtmlstring")]
        public IActionResult InspectXhtmlString([FromQuery] int contentId = 883)
        {
            try
            {
                var contentRef = new ContentReference(contentId);
                if (!_contentLoader.TryGet<IContent>(contentRef, out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                var xhtmlStringProps = new List<object>();
                foreach (var prop in content.Property.Where(p => p is PropertyXhtmlString))
                {
                    var xhtmlProp = prop as PropertyXhtmlString;
                    var xhtml = xhtmlProp?.XhtmlString;
                    if (xhtml == null) continue;

                    var fragments = new List<object>();
                    if (xhtml.Fragments != null)
                    {
                        foreach (var fragment in xhtml.Fragments)
                        {
                            fragments.Add(new
                            {
                                FragmentType = fragment.GetType().Name,
                                InternalFormat = fragment.InternalFormat,
                                IsContentFragment = fragment is EPiServer.Core.Html.StringParsing.ContentFragment,
                                ContentLink = (fragment as EPiServer.Core.Html.StringParsing.ContentFragment)?.ContentLink?.ID,
                            });
                        }
                    }

                    var htmlOutput = xhtml.ToHtmlString();
                    var containsImg = htmlOutput?.Contains("<img", StringComparison.OrdinalIgnoreCase) ?? false;

                    xhtmlStringProps.Add(new
                    {
                        PropertyName = prop.Name,
                        FragmentCount = fragments.Count,
                        Fragments = fragments,
                        HtmlOutput = htmlOutput,
                        ContainsImgTag = containsImg,
                        Explanation = containsImg
                            ? "IMAGE FOUND in XhtmlString — stored as raw HTML <img> tag. Alt text is whatever was in the HTML attribute, NOT from the ImageMediaData.AltText property."
                            : "No <img> tag found in this XhtmlString property."
                    });
                }

                return Ok(new
                {
                    Step = "3 — Inspect XhtmlString",
                    ContentId = contentId,
                    ContentName = content.Name,
                    ContentType = content.GetType().Name,
                    XhtmlStringProperties = xhtmlStringProps,
                    KeyInsight = "Images in XhtmlString are stored as StaticFragment with raw HTML. They are NOT ContentFragment references. This means Content Graph cannot 'Expand' them to get image metadata."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Compare how image data is exposed across different property types on the same page.
        /// Shows XhtmlString (raw HTML only) vs ContentReference (full Expanded metadata) vs ContentArea.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/compare-property-types?contentId=883
        /// </summary>
        [HttpGet("compare-property-types")]
        public IActionResult ComparePropertyTypes([FromQuery] int contentId = 883)
        {
            try
            {
                var contentRef = new ContentReference(contentId);
                if (!_contentLoader.TryGet<IContent>(contentRef, out var content))
                {
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });
                }

                // 1. XhtmlString — MainBody
                object xhtmlStringResult = null;
                if (content.Property["MainBody"] is PropertyXhtmlString mainBodyProp && mainBodyProp.XhtmlString != null)
                {
                    var html = mainBodyProp.XhtmlString.ToHtmlString();
                    xhtmlStringResult = new
                    {
                        PropertyType = "XhtmlString",
                        PropertyName = "MainBody",
                        HtmlOutput = html,
                        ContainsImgTag = html?.Contains("<img", StringComparison.OrdinalIgnoreCase) ?? false,
                        ImageMetadataAvailable = false,
                        WhyNot = "XhtmlString stores images as raw HTML <img> tags. Content Graph returns this HTML as-is. No Expanded query possible."
                    };
                }

                // 2. ContentReference — PageImage
                object contentRefResult = null;
                var pageImageProp = content.Property["PageImage"];
                if (pageImageProp?.Value != null)
                {
                    var imageRef = pageImageProp.Value as ContentReference;
                    if (imageRef != null && !ContentReference.IsNullOrEmpty(imageRef) && _contentLoader.TryGet<ImageMediaData>(imageRef, out var imageData))
                    {
                        contentRefResult = new
                        {
                            PropertyType = "ContentReference",
                            PropertyName = "PageImage",
                            ImageContentId = imageData.ContentLink.ID,
                            ImageName = imageData.Name,
                            AltText = imageData.AltText,
                            Title = imageData.Title,
                            Description = imageData.Description,
                            Caption = imageData.Caption,
                            Copyright = imageData.Copyright,
                            ImageMetadataAvailable = true,
                            GraphQuery = "PageImage { Expanded { ... on ImageMediaData { AltText Title Description } } }"
                        };
                    }
                }

                // 3. ContentArea — MainContentArea
                object contentAreaResult = null;
                var contentAreaProp = content.Property["MainContentArea"];
                if (contentAreaProp?.Value is ContentArea contentArea && contentArea.FilteredItems.Any())
                {
                    var areaItems = contentArea.FilteredItems.Take(5).Select(item =>
                    {
                        _contentLoader.TryGet<IContent>(item.ContentLink, out var areaContent);
                        if (areaContent is ImageMediaData img)
                        {
                            return (object)new
                            {
                                ContentId = img.ContentLink.ID,
                                Type = "ImageMediaData",
                                AltText = img.AltText,
                                Title = img.Title,
                                ImageMetadataAvailable = true,
                            };
                        }
                        return new
                        {
                            ContentId = areaContent?.ContentLink?.ID ?? 0,
                            Type = areaContent?.GetType()?.Name ?? "Unknown",
                            AltText = (string)null,
                            Title = (string)null,
                            ImageMetadataAvailable = false,
                        };
                    }).ToList();

                    contentAreaResult = new
                    {
                        PropertyType = "ContentArea",
                        PropertyName = "MainContentArea",
                        Items = areaItems,
                        GraphQuery = "MainContentArea { ContentLink { Expanded { ... on ImageMediaData { AltText Title } } } }"
                    };
                }

                return Ok(new
                {
                    Step = "4 — Compare Property Types",
                    ContentId = contentId,
                    ContentName = content.Name,
                    XhtmlString_MainBody = xhtmlStringResult ?? (object)new { Note = "MainBody is empty or not found" },
                    ContentReference_PageImage = contentRefResult ?? (object)new { Note = "PageImage is empty or not found" },
                    ContentArea_MainContentArea = contentAreaResult ?? (object)new { Note = "MainContentArea is empty or not found" },
                    Conclusion = new
                    {
                        XhtmlString = "Returns raw HTML only. Image alt/title/metadata NOT accessible via Graph query.",
                        ContentReference = "Returns content reference that can be Expanded to get ALL image properties (Alt, Title, etc.).",
                        ContentArea = "Each item is a content reference that can be Expanded — same as ContentReference.",
                        Recommendation = "For images that need metadata in Graph queries, use ContentReference or ContentArea instead of embedding in XhtmlString."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Query Content Graph directly via GraphQL to show what XhtmlString Html returns for a specific content.
        /// Demonstrates that the Html field only contains raw markup with no expanded image properties.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/query-graph-html?contentId=883
        /// </summary>
        [HttpGet("query-graph-html")]
        public async Task<IActionResult> QueryGraphHtml([FromQuery] int contentId = 886)
        {
            try
            {
                var singleKey = _configuration["Optimizely:ContentGraph:SingleKey"];
                var gateway = _configuration["Optimizely:ContentGraph:GatewayAddress"] ?? "https://cg.optimizely.com";

                if (string.IsNullOrWhiteSpace(singleKey))
                {
                    return BadRequest(new { Error = "SingleKey not configured. Set Optimizely:ContentGraph:SingleKey in appsettings." });
                }

                var url = $"{gateway.TrimEnd('/')}/content/v2?auth={singleKey}";
                using var httpClient = new HttpClient();

                // Query A: MainBody as plain String (default RichTextFormat=Html)
                var queryAsString = @"{
                    StandardPage(where: { ContentLink: { Id: { eq: " + contentId + @" } } }) {
                        total
                        items {
                            Name
                            MainBody
                            PageImage { Id Url Expanded { ... on ImageMediaData { AltText Title Description Caption } } }
                        }
                    }
                }";

                var bodyA = new StringContent(JsonSerializer.Serialize(new { query = queryAsString }), Encoding.UTF8, "application/json");
                var respA = await httpClient.PostAsync(url, bodyA);
                var resultA = await respA.Content.ReadAsStringAsync();

                // Query B: MainBody as complex type (when RichTextFormat=HtmlAndStructured)
                var queryAsObject = @"{
                    StandardPage(where: { ContentLink: { Id: { eq: " + contentId + @" } } }) {
                        total
                        items {
                            Name
                            MainBody { Html }
                            PageImage { Id Url Expanded { ... on ImageMediaData { AltText Title Description Caption } } }
                        }
                    }
                }";

                var bodyB = new StringContent(JsonSerializer.Serialize(new { query = queryAsObject }), Encoding.UTF8, "application/json");
                var respB = await httpClient.PostAsync(url, bodyB);
                var resultB = await respB.Content.ReadAsStringAsync();

                // Query C: Directly query the image to show its full properties
                var pageContent = _contentLoader.TryGet<IContent>(new ContentReference(contentId), out var c) ? c : null;
                var imageId = (pageContent?.Property["PageImage"]?.Value as ContentReference)?.ID ?? 0;
                var queryImage = @"{
                    ImageMediaData(where: { ContentLink: { Id: { eq: " + imageId + @" } } }) {
                        items { Name AltText Title Description Caption Copyright CreditsText }
                    }
                }";

                var bodyC = new StringContent(JsonSerializer.Serialize(new { query = queryImage }), Encoding.UTF8, "application/json");
                var respC = await httpClient.PostAsync(url, bodyC);
                var resultC = await respC.Content.ReadAsStringAsync();

                return Ok(new
                {
                    Step = "5 — Query Content Graph",
                    ContentId = contentId,
                    CurrentRichTextFormat = _contentApiOptions.RichTextFormat.ToString(),
                    QueryA_MainBodyAsString = new
                    {
                        Note = "MainBody treated as plain String (RichTextFormat=Html mode)",
                        GraphQLQuery = queryAsString.Trim(),
                        Response = TryParseJson(resultA),
                        HttpStatus = (int)respA.StatusCode,
                    },
                    QueryB_MainBodyAsObject = new
                    {
                        Note = "MainBody treated as complex type with Html sub-field (RichTextFormat=HtmlAndStructured mode)",
                        GraphQLQuery = queryAsObject.Trim(),
                        Response = TryParseJson(resultB),
                        HttpStatus = (int)respB.StatusCode,
                    },
                    QueryC_DirectImageQuery = new
                    {
                        Note = "Direct query of the ImageMediaData — ALL properties are available here",
                        ImageContentId = imageId,
                        GraphQLQuery = queryImage.Trim(),
                        Response = TryParseJson(resultC),
                        HttpStatus = (int)respC.StatusCode,
                    },
                    Analysis = new
                    {
                        MainBody = "Returns raw HTML string with <img alt='' />. The alt is empty because TinyMCE did not populate it from image metadata.",
                        PageImage_Expanded = "Returns full image metadata via Expanded: AltText, Title, Description, etc.",
                        DirectImageQuery = "All image properties are queryable when the image is queried as its own content type.",
                        RootCause = "XhtmlString flattens image references into HTML markup. Graph has no mechanism to resolve embedded <img> tags back to content references for Expanded queries."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }


        /// <summary>
        /// Step 6: Create a test StandardPage with the same image in both XhtmlString and ContentReference.
        /// This makes it easy to compare the Graph output side by side.
        /// Sample usage: https://localhost:5009/util-api/custom-xhtmlstring-image/setup-test-content?imageId=637
        /// </summary>
        [HttpGet("setup-test-content")]
        public IActionResult SetupTestContent([FromQuery] int imageId = 637, [FromQuery] int parentId = 0)
        {
            try
            {
                // Find an image to use
                ImageMediaData imageData = null;
                if (imageId > 0)
                {
                    _contentLoader.TryGet(new ContentReference(imageId), out imageData);
                }

                if (imageData == null)
                {
                    // Find any image
                    var allRefs = _contentLoader.GetDescendents(ContentReference.RootPage).Take(500);
                    foreach (var r in allRefs)
                    {
                        if (_contentLoader.TryGet<ImageMediaData>(r, out var img))
                        {
                            imageData = img;
                            break;
                        }
                    }
                }

                if (imageData == null)
                {
                    return BadRequest(new { Error = "No ImageMediaData found. Upload an image first." });
                }

                // Ensure AltText is set on the image
                if (string.IsNullOrEmpty(imageData.AltText))
                {
                    var writableImage = imageData.CreateWritableClone() as ImageMediaData;
                    writableImage.AltText = "Test Alt Text for Graph Investigation";
                    writableImage.Title = "Test Image Title";
                    writableImage.Description = "Test image description for XhtmlString vs ContentReference comparison";
                    _contentRepository.Save(writableImage, EPiServer.DataAccess.SaveAction.Publish, EPiServer.Security.AccessLevel.NoAccess);
                    imageData = writableImage;
                }

                // Create test page
                var parentRef = parentId > 0 ? new ContentReference(parentId) : ContentReference.StartPage;
                var page = _contentRepository.GetDefault<Features.StandardPage.StandardPage>(parentRef);
                page.Name = $"XhtmlString-Image-Test-{DateTime.Now:yyyyMMdd-HHmmss}";

                // Set XhtmlString with embedded image (raw HTML img tag — same as TinyMCE does)
                var imageUrl = UrlResolver.Current.GetUrl(imageData.ContentLink);
                var xhtml = new XhtmlString($"<p>Text before image.</p><p><img src=\"{imageUrl}\" alt=\"\" width=\"800\" height=\"600\" /></p><p>Text after image.</p>");
                page.MainBody = xhtml;

                // Set ContentReference to same image
                page.PageImage = imageData.ContentLink;

                _contentRepository.Save(page, EPiServer.DataAccess.SaveAction.Publish, EPiServer.Security.AccessLevel.NoAccess);

                return Ok(new
                {
                    Step = "6 — Setup Test Content",
                    CreatedPage = new
                    {
                        ContentId = page.ContentLink.ID,
                        Name = page.Name,
                        ParentId = parentRef.ID
                    },
                    ImageUsed = new
                    {
                        ContentId = imageData.ContentLink.ID,
                        Name = imageData.Name,
                        AltText = imageData.AltText,
                        Title = imageData.Title,
                        Description = imageData.Description
                    },
                    XhtmlString_MainBody = page.MainBody.ToHtmlString(),
                    ContentReference_PageImage = page.PageImage.ID,
                    NextSteps = new[]
                    {
                        $"1. Run Graph sync job or wait for auto-sync",
                        $"2. Inspect: https://localhost:5009/util-api/custom-xhtmlstring-image/inspect-xhtmlstring?contentId={page.ContentLink.ID}",
                        $"3. Compare: https://localhost:5009/util-api/custom-xhtmlstring-image/compare-property-types?contentId={page.ContentLink.ID}",
                        $"4. Query Graph: https://localhost:5009/util-api/custom-xhtmlstring-image/query-graph-html?contentId={page.ContentLink.ID}",
                    },
                    ExpectedBehavior = new
                    {
                        MainBody_Html = "Will return raw HTML: <img src='...' alt='' /> — empty alt, no AltText/Title/Description from ImageMediaData",
                        PageImage_Expanded = "Will return full metadata: { AltText: 'Test Alt Text...', Title: 'Test Image Title', Description: '...' }",
                        RootCause = "XhtmlString stores images as HTML markup, not as content references. Graph cannot Expand HTML tags."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Helpers

        private static object TryParseJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new { IsEmpty = true };
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.Clone();
            }
            catch
            {
                return new { IsJson = false, Raw = raw };
            }
        }

        #endregion
    }
}
