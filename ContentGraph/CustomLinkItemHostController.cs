using System.Globalization;
using System.Text.Json;
using EPiServer.ContentApi.Core.Configuration;
using EPiServer.ContentApi.Core.Serialization;
using EPiServer.ContentApi.Core.Serialization.Models;
using EPiServer.DataAccess;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using Foundation.Features.Blocks.BootstrapCardBlock;
using Foundation.Features.StandardPage;
using Mediachase.Commerce.Catalog;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Cms.Core;
using Optimizely.ContentGraph.Cms.Core.Internal;
using Optimizely.ContentGraph.Cms.NetCore.Core;
using Optimizely.ContentGraph.Cms.NetCore.Core.Internal;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to replicate the LinkItem URL host mismatch issue
    /// for commerce catalog content in multisite Content Graph serialization.
    /// ContentReference resolves the correct host, but LinkItem.Href can use the wrong site host.
    /// Base route: util-api/custom-link-item-host
    /// </summary>
    [ApiController]
    [Route("util-api/custom-link-item-host")]
    public class CustomLinkItemHostController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IUrlResolver _urlResolver;
        private readonly ISiteDefinitionRepository _siteDefinitionRepository;
        private readonly ISiteDefinitionResolver _siteDefinitionResolver;
        private readonly ReferenceConverter _referenceConverter;
        private readonly IContentConverterProvider _contentConverterProvider;
        private readonly ContentApiOptions _contentApiOptions;
        private readonly IContentRepository _contentRepository;
        private readonly IContentSerializer _contentSerializer;
        private readonly ContentApiModelTransformer _contentApiModelTransformer;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IContentIndexer _contentIndexer;
        private readonly IOptions<QueryOptions> _queryOptions;

        // Track the second site and test block we create so we can clean them up
        private static Guid _createdSiteId = Guid.Empty;
        private static ContentReference _testBlockId = ContentReference.EmptyReference;
        private static ContentReference _testPageId = ContentReference.EmptyReference;
        private static ContentReference _siteBStartPageId = ContentReference.EmptyReference;

        public CustomLinkItemHostController(
            IContentLoader contentLoader,
            IUrlResolver urlResolver,
            ISiteDefinitionRepository siteDefinitionRepository,
            ISiteDefinitionResolver siteDefinitionResolver,
            ReferenceConverter referenceConverter,
            IContentConverterProvider contentConverterProvider,
            ContentApiOptions contentApiOptions,
            IContentRepository contentRepository,
            IContentSerializer contentSerializer,
            ContentApiModelTransformer contentApiModelTransformer,
            IContentTypeRepository contentTypeRepository,
            IContentIndexer contentIndexer,
            IOptions<QueryOptions> queryOptions)
        {
            _contentLoader = contentLoader;
            _urlResolver = urlResolver;
            _siteDefinitionRepository = siteDefinitionRepository;
            _siteDefinitionResolver = siteDefinitionResolver;
            _referenceConverter = referenceConverter;
            _contentConverterProvider = contentConverterProvider;
            _contentApiOptions = contentApiOptions;
            _contentRepository = contentRepository;
            _contentSerializer = contentSerializer;
            _contentApiModelTransformer = contentApiModelTransformer;
            _contentTypeRepository = contentTypeRepository;
            _contentIndexer = contentIndexer;
            _queryOptions = queryOptions;
        }

        /// <summary>
        /// Step 0: Shows the current configuration, all available sites, and sample URLs for each step.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                var sites = _siteDefinitionRepository.List()
                    .Select(s => new
                    {
                        s.Name,
                        s.Id,
                        SiteUrl = s.SiteUrl?.ToString(),
                        Hosts = s.Hosts?.Select(h => new { h.Name, Type = h.Type.ToString() })
                    }).ToList();

                return Ok(new
                {
                    Description = "LinkItem Host Mismatch Repro — Commerce content in multisite Content Graph serialization",
                    Issue = "ContentReference URL resolves correct host, but LinkItem.Href can use the wrong site host during Graph serialization",
                    ConfiguredSites = sites,
                    SecondSiteCreated = _createdSiteId != Guid.Empty,
                    Steps = new[]
                    {
                        "Step 1: GET /setup-multisite — Create a second site definition with host 'site-b.local'",
                        "Step 2: GET /list-sites — List all site definitions and catalog nodes",
                        "Step 3: GET /resolve-content-reference?nodeCode=mens — Resolve commerce node URL via ContentReference + IUrlResolver under each site context",
                        "Step 4: GET /resolve-link-item?nodeCode=mens — Create LinkItem, resolve via GetMappedHref under each site context",
                        "Step 5: GET /create-test-block?nodeCode=mens — Create a BootstrapCardBlock with CardLinks pointing to commerce node",
                        "Step 6: GET /create-site-b-test-page — Create a real page under the second site with the test block in MainContentArea",
                        "Step 7: GET /serialize-test-block — Serialize the test block via Content Delivery API under each site context",
                        "Step 8: GET /graph-serialize-test-content?target=page — Build the real Content Graph JSON document for the test page",
                        "Step 9: GET /index-test-content?target=page — Send the test page through the real Content Graph indexer",
                        "Step 10: GET /workaround-rewrite-host?nodeCode=mens — Apply ISiteDefinitionResolver workaround to fix LinkItem host",
                        "Step 11: GET /cleanup — Remove the second site definition and test content",
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-link-item-host/setup-multisite",
                        "https://localhost:5009/util-api/custom-link-item-host/list-sites",
                        "https://localhost:5009/util-api/custom-link-item-host/resolve-content-reference?nodeCode=mens",
                        "https://localhost:5009/util-api/custom-link-item-host/resolve-link-item?nodeCode=mens",
                        "https://localhost:5009/util-api/custom-link-item-host/create-test-block?nodeCode=mens",
                        "https://localhost:5009/util-api/custom-link-item-host/create-site-b-test-page",
                        "https://localhost:5009/util-api/custom-link-item-host/serialize-test-block",
                        "https://localhost:5009/util-api/custom-link-item-host/graph-serialize-test-content?target=page",
                        "https://localhost:5009/util-api/custom-link-item-host/index-test-content?target=page",
                        "https://localhost:5009/util-api/custom-link-item-host/workaround-rewrite-host?nodeCode=mens",
                        "https://localhost:5009/util-api/custom-link-item-host/cleanup",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Create a second site definition with its OWN start page and host 'site-b.local'.
        /// A separate start page is critical — ISiteDefinitionResolver uses start page ancestry to determine
        /// which site owns which content. Without separate start pages, the resolver can't distinguish sites.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/setup-multisite
        /// </summary>
        [HttpGet("setup-multisite")]
        public IActionResult SetupMultisite(
            [FromQuery] string siteName = "site-b",
            [FromQuery] string host = "site-b.local")
        {
            try
            {
                // Check if already created
                var existing = _siteDefinitionRepository.List()
                    .FirstOrDefault(s => s.Name == siteName);
                if (existing != null)
                {
                    _createdSiteId = existing.Id;
                    _siteBStartPageId = existing.StartPage ?? ContentReference.EmptyReference;

                    return Ok(new
                    {
                        Step = "1 — Setup Multisite",
                        AlreadyExists = true,
                        SiteId = existing.Id,
                        SiteName = existing.Name,
                        SiteUrl = existing.SiteUrl?.ToString(),
                        StartPage = existing.StartPage?.ToString(),
                        Hosts = existing.Hosts?.Select(h => new { h.Name, Type = h.Type.ToString() }),
                        Message = $"Site '{siteName}' already exists. Call /cleanup first to recreate."
                    });
                }

                // Create a NEW start page for site-b under RootPage (not under foundation's start page)
                var siteBStartPage = _contentRepository.GetDefault<StandardPage>(ContentReference.RootPage);
                (siteBStartPage as IContent).Name = $"Site-B-Start-{DateTime.Now:yyyyMMddHHmmss}";
                var siteBStartPageRef = _contentRepository.Save(siteBStartPage as IContent, SaveAction.Publish, AccessLevel.NoAccess);
                _siteBStartPageId = siteBStartPageRef;

                // Create a new site definition with a DIFFERENT start page and host
                var newSite = new SiteDefinition
                {
                    Name = siteName,
                    SiteUrl = new Uri($"https://{host}/"),
                    StartPage = siteBStartPageRef  // Different start page from foundation!
                };

                newSite.Hosts.Add(new HostDefinition
                {
                    Name = host,
                    Type = HostDefinitionType.Primary
                });

                _siteDefinitionRepository.Save(newSite);
                _createdSiteId = newSite.Id;

                var primarySite = _siteDefinitionRepository.List().First();

                var allSites = _siteDefinitionRepository.List()
                    .Select(s => new
                    {
                        s.Name,
                        s.Id,
                        SiteUrl = s.SiteUrl?.ToString(),
                        StartPage = s.StartPage?.ToString(),
                        Hosts = s.Hosts?.Select(h => new { h.Name, Type = h.Type.ToString() })
                    }).ToList();

                return Ok(new
                {
                    Step = "1 — Setup Multisite",
                    Created = true,
                    NewSite = new
                    {
                        newSite.Name,
                        newSite.Id,
                        SiteUrl = newSite.SiteUrl?.ToString(),
                        StartPage = newSite.StartPage?.ToString()
                    },
                    FoundationStartPage = primarySite.StartPage?.ToString(),
                    SiteBStartPage = siteBStartPageRef.ToString(),
                    AllSites = allSites,
                    Message = $"Site '{siteName}' created with SEPARATE start page ({siteBStartPageRef}) and host '{host}'. " +
                              "Commerce catalog content is owned by foundation (start page 15), NOT by site-b."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: List all site definitions with their hosts and all catalog nodes.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/list-sites
        /// </summary>
        [HttpGet("list-sites")]
        public IActionResult ListSites()
        {
            try
            {
                var sites = _siteDefinitionRepository.List().Select(s => new
                {
                    s.Name,
                    s.Id,
                    SiteUrl = s.SiteUrl?.ToString(),
                    StartPage = s.StartPage?.ToString(),
                    Hosts = s.Hosts?.Select(h => new
                    {
                        h.Name,
                        Type = h.Type.ToString(),
                        Url = h.Url?.ToString()
                    })
                }).ToList();

                var catalogRoot = _referenceConverter.GetRootLink();
                var catalogs = _contentLoader.GetChildren<CatalogContentBase>(catalogRoot).ToList();
                var catalogInfo = new List<object>();
                foreach (var catalog in catalogs)
                {
                    var nodes = _contentLoader.GetChildren<NodeContent>(catalog.ContentLink).ToList();
                    catalogInfo.Add(new
                    {
                        CatalogName = catalog.Name,
                        CatalogLink = catalog.ContentLink.ToString(),
                        Nodes = nodes.Select(n => new
                        {
                            n.Name,
                            n.Code,
                            ContentLink = n.ContentLink.ToString()
                        })
                    });
                }

                return Ok(new
                {
                    Step = "2 — List Sites & Catalogs",
                    Sites = sites,
                    Catalogs = catalogInfo,
                    CurrentSite = SiteDefinition.Current?.Name,
                    SecondSiteCreated = _createdSiteId != Guid.Empty
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Resolve a commerce catalog node URL via ContentReference + IUrlResolver under each site context.
        /// Switches SiteDefinition.Current to each site and calls IUrlResolver.GetUrl to show real resolution.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/resolve-content-reference?nodeCode=mens
        /// </summary>
        [HttpGet("resolve-content-reference")]
        public IActionResult ResolveContentReference([FromQuery] string nodeCode = "mens")
        {
            try
            {
                var contentLink = _referenceConverter.GetContentLink(nodeCode);
                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return BadRequest(new { Error = $"No commerce content found for code '{nodeCode}'." });
                }

                var content = _contentLoader.Get<IContent>(contentLink);
                var originalSite = SiteDefinition.Current;
                var allSites = _siteDefinitionRepository.List().ToList();

                var perSiteResults = new List<object>();
                foreach (var site in allSites)
                {
                    SiteDefinition.Current = site;

                    var resolvedUrl = _urlResolver.GetUrl(contentLink);

                    var primaryHost = site.Hosts?
                        .FirstOrDefault(h => h.Type == HostDefinitionType.Primary)?.Url
                        ?? site.SiteUrl;

                    string absoluteUrl = null;
                    if (primaryHost != null && !string.IsNullOrEmpty(resolvedUrl))
                    {
                        Uri.TryCreate(primaryHost, resolvedUrl, out var fullUri);
                        absoluteUrl = fullUri?.ToString();
                    }

                    perSiteResults.Add(new
                    {
                        SiteName = site.Name,
                        SiteUrl = site.SiteUrl?.ToString(),
                        PrimaryHost = primaryHost?.ToString(),
                        ResolvedUrl = resolvedUrl,
                        AbsoluteUrl = absoluteUrl
                    });
                }

                SiteDefinition.Current = originalSite;

                return Ok(new
                {
                    Step = "3 — Resolve ContentReference URL (per site context)",
                    NodeCode = nodeCode,
                    ContentLink = contentLink.ToString(),
                    ContentType = content.GetOriginalType().Name,
                    ContentName = content.Name,
                    PerSiteResults = perSiteResults
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Create a LinkItem pointing to the commerce node, resolve via GetMappedHref under each site context.
        /// Uses real PermanentLinkUtility and LinkItem.GetMappedHref() — the same path the serializer uses.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/resolve-link-item?nodeCode=mens
        /// </summary>
        [HttpGet("resolve-link-item")]
        public IActionResult ResolveLinkItem([FromQuery] string nodeCode = "mens")
        {
            try
            {
                var contentLink = _referenceConverter.GetContentLink(nodeCode);
                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return BadRequest(new { Error = $"No commerce content found for code '{nodeCode}'." });
                }

                var content = _contentLoader.Get<IContent>(contentLink);

                var permanentGuid = PermanentLinkUtility.FindGuid(contentLink);
                var permanentLinkUrl = PermanentLinkUtility.GetPermanentLinkVirtualPath(permanentGuid, ".aspx");

                var linkItem = new LinkItem
                {
                    Href = permanentLinkUrl,
                    Text = content.Name,
                    Title = $"Link to {content.Name}"
                };

                var originalSite = SiteDefinition.Current;
                var allSites = _siteDefinitionRepository.List().ToList();

                var perSiteResults = new List<object>();
                foreach (var site in allSites)
                {
                    SiteDefinition.Current = site;

                    var mappedHref = linkItem.GetMappedHref();
                    var urlResolverResult = _urlResolver.GetUrl(contentLink);

                    var primaryHost = site.Hosts?
                        .FirstOrDefault(h => h.Type == HostDefinitionType.Primary)?.Url
                        ?? site.SiteUrl;

                    string absoluteMappedHref = null;
                    if (primaryHost != null && !string.IsNullOrEmpty(mappedHref))
                    {
                        Uri.TryCreate(primaryHost, mappedHref, out var fullUri);
                        absoluteMappedHref = fullUri?.ToString();
                    }

                    perSiteResults.Add(new
                    {
                        SiteName = site.Name,
                        SiteUrl = site.SiteUrl?.ToString(),
                        PrimaryHost = primaryHost?.ToString(),
                        MappedHref = mappedHref,
                        AbsoluteMappedHref = absoluteMappedHref,
                        UrlResolverResult = urlResolverResult
                    });
                }

                SiteDefinition.Current = originalSite;

                return Ok(new
                {
                    Step = "4 — Resolve LinkItem (per site context)",
                    NodeCode = nodeCode,
                    ContentLink = contentLink.ToString(),
                    ContentName = content.Name,
                    PermanentLinkGuid = permanentGuid,
                    PermanentLinkUrl = permanentLinkUrl,
                    LinkItem = new
                    {
                        linkItem.Href,
                        linkItem.Text,
                        linkItem.Title
                    },
                    PerSiteResults = perSiteResults
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Programmatically create a BootstrapCardBlock with CardLinks (LinkItemCollection)
        /// containing a permanent link to the commerce catalog node. This gives us real content
        /// with a populated LinkItemCollection pointing to commerce content.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/create-test-block?nodeCode=mens
        /// </summary>
        [HttpGet("create-test-block")]
        public IActionResult CreateTestBlock([FromQuery] string nodeCode = "mens")
        {
            try
            {
                // Check if already created
                if (!ContentReference.IsNullOrEmpty(_testBlockId))
                {
                    var existingBlock = _contentLoader.Get<IContent>(_testBlockId);
                    return Ok(new
                    {
                        Step = "5 — Create Test Block",
                        AlreadyExists = true,
                        TestBlockId = _testBlockId.ToString(),
                        TestBlockName = existingBlock?.Name,
                        Message = "Test block already exists. Call /cleanup first to recreate."
                    });
                }

                var contentLink = _referenceConverter.GetContentLink(nodeCode);
                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return BadRequest(new { Error = $"No commerce content found for code '{nodeCode}'." });
                }

                var commerceContent = _contentLoader.Get<IContent>(contentLink);

                // Get the permanent link for the commerce content
                var permanentGuid = PermanentLinkUtility.FindGuid(contentLink);
                var permanentLinkUrl = PermanentLinkUtility.GetPermanentLinkVirtualPath(permanentGuid, ".aspx");

                // Create a BootstrapCardBlock under Global Block folder
                var testBlock = _contentRepository.GetDefault<BootstrapCardBlock>(ContentReference.GlobalBlockFolder);
                (testBlock as IContent).Name = $"LinkItem-Host-Test-{DateTime.Now:yyyyMMddHHmmss}";
                testBlock.CardTitle = $"Test block linking to {commerceContent.Name}";

                // Populate CardLinks with a LinkItem pointing to commerce content via permanent link
                var linkItemCollection = new LinkItemCollection();
                var linkItem = new LinkItem
                {
                    Href = permanentLinkUrl,
                    Text = $"Link to {commerceContent.Name}",
                    Title = $"Commerce link to {nodeCode}"
                };
                linkItemCollection.Add(linkItem);
                testBlock.CardLinks = linkItemCollection;

                // Save and publish
                var savedRef = _contentRepository.Save(testBlock as IContent, SaveAction.Publish, AccessLevel.NoAccess);
                _testBlockId = savedRef;

                // Verify the saved content
                var savedBlock = _contentLoader.Get<IContent>(savedRef);
                var savedCardLinks = (savedBlock as BootstrapCardBlock)?.CardLinks;

                return Ok(new
                {
                    Step = "5 — Create Test Block",
                    Created = true,
                    TestBlockId = savedRef.ToString(),
                    TestBlockName = savedBlock.Name,
                    CommerceTarget = new
                    {
                        NodeCode = nodeCode,
                        ContentLink = contentLink.ToString(),
                        ContentName = commerceContent.Name,
                        PermanentLinkGuid = permanentGuid,
                        PermanentLinkUrl = permanentLinkUrl
                    },
                    SavedCardLinks = savedCardLinks?.Select(li => new
                    {
                        li.Href,
                        li.Text,
                        li.Title,
                        MappedHref = li.GetMappedHref()
                    }),
                    Message = "Test block created with CardLinks pointing to commerce content. Next call /create-site-b-test-page so the block is indexed from real second-site content."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Create a real page under the second site and place the test block in MainContentArea.
        /// This is closer to the reported multisite Content Graph path than indexing a global block directly.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/create-site-b-test-page
        /// </summary>
        [HttpGet("create-site-b-test-page")]
        public IActionResult CreateSiteBTestPage()
        {
            try
            {
                if (ContentReference.IsNullOrEmpty(_testBlockId))
                {
                    return BadRequest(new { Error = "No test block created. Call /create-test-block first." });
                }

                if (!ContentReference.IsNullOrEmpty(_testPageId))
                {
                    var existingPage = _contentLoader.Get<IContent>(_testPageId);
                    return Ok(new
                    {
                        Step = "6 - Create Site-B Test Page",
                        AlreadyExists = true,
                        TestPageId = _testPageId.ToString(),
                        TestPageName = existingPage?.Name,
                        Message = "Test page already exists. Call /cleanup first to recreate."
                    });
                }

                var siteB = _siteDefinitionRepository.List().FirstOrDefault(s => s.Name == "site-b");
                if (siteB == null)
                {
                    return BadRequest(new { Error = "Second site was not found. Call /setup-multisite first." });
                }

                _createdSiteId = siteB.Id;
                _siteBStartPageId = siteB.StartPage ?? ContentReference.EmptyReference;
                if (ContentReference.IsNullOrEmpty(_siteBStartPageId))
                {
                    return BadRequest(new { Error = "Second site has no start page. Call /cleanup then /setup-multisite to recreate it." });
                }

                var testPage = _contentRepository.GetDefault<StandardPage>(_siteBStartPageId);
                testPage.PageName = $"Site-B-LinkItem-Host-Test-{DateTime.Now:yyyyMMddHHmmss}";
                testPage.MetaTitle = testPage.PageName;
                testPage.PageDescription = "Site-B page used to index a nested LinkItemCollection through the real Content Graph serializer.";
                testPage.MainContentArea = new ContentArea();
                testPage.MainContentArea.Items.Add(new ContentAreaItem
                {
                    ContentLink = _testBlockId
                });

                var savedPageRef = _contentRepository.Save(testPage, SaveAction.Publish, AccessLevel.NoAccess);
                _testPageId = savedPageRef;

                var owningSite = _siteDefinitionResolver.GetByContent(savedPageRef, true, true);
                var pageUrl = _urlResolver.GetUrl(savedPageRef);
                var block = _contentLoader.Get<IContent>(_testBlockId);

                return Ok(new
                {
                    Step = "6 - Create Site-B Test Page",
                    Created = true,
                    TestPageId = savedPageRef.ToString(),
                    TestPageName = testPage.PageName,
                    TestPageUrl = pageUrl,
                    OwningSite = new
                    {
                        owningSite?.Name,
                        owningSite?.Id,
                        SiteUrl = owningSite?.SiteUrl?.ToString(),
                        StartPage = owningSite?.StartPage?.ToString()
                    },
                    EmbeddedBlock = new
                    {
                        TestBlockId = _testBlockId.ToString(),
                        block.Name
                    },
                    Message = "Site-B test page created. Next call /graph-serialize-test-content?target=page."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Serialize the test block created in Step 5 via the Content Delivery API
        /// converter under each site context. Also resolves ALL registered IContentConverterProvider
        /// implementations to check if the Content Graph's own converter produces different output.
        /// Compare the CardLinks Href values across site contexts to see the host mismatch.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/serialize-test-block
        /// </summary>
        [HttpGet("serialize-test-block")]
        public IActionResult SerializeTestBlock([FromServices] IEnumerable<IContentConverterProvider> allConverterProviders)
        {
            try
            {
                if (ContentReference.IsNullOrEmpty(_testBlockId))
                {
                    return BadRequest(new { Error = "No test block created. Call /create-test-block first." });
                }

                var content = _contentLoader.Get<IContent>(_testBlockId);
                var originalSite = SiteDefinition.Current;
                var allSites = _siteDefinitionRepository.List().ToList();

                // Log all registered converter providers
                var converterProviderTypes = allConverterProviders
                    .Select(p => p.GetType().FullName)
                    .ToList();

                var perSiteResults = new List<object>();
                foreach (var site in allSites)
                {
                    SiteDefinition.Current = site;

                    var perConverterResults = new List<object>();
                    foreach (var converterProvider in allConverterProviders)
                    {
                        try
                        {
                            var converter = converterProvider.Resolve(content);
                            var converterContext = new ConverterContext(
                                _contentApiOptions,
                                "",
                                "",
                                false,
                                CultureInfo.GetCultureInfo("en")
                            );

                            var apiModel = converter.Convert(content, converterContext);

                            // Extract CardLinks Href values
                            var cardLinksHrefs = new List<string>();
                            if (apiModel.Properties.TryGetValue("CardLinks", out var cardLinksValue))
                            {
                                var serialized = JsonConvert.SerializeObject(cardLinksValue);
                                // Quick extract of Href values from the serialized JSON
                                cardLinksHrefs = System.Text.RegularExpressions.Regex
                                    .Matches(serialized, "\"[Hh]ref\"\\s*:\\s*\"([^\"]+)\"")
                                    .Cast<System.Text.RegularExpressions.Match>()
                                    .Select(m => m.Groups[1].Value)
                                    .Distinct()
                                    .ToList();
                            }

                            perConverterResults.Add(new
                            {
                                ConverterType = converterProvider.GetType().FullName,
                                ActualConverterType = converter.GetType().FullName,
                                ApiModelUrl = apiModel.Url,
                                CardLinksHrefs = cardLinksHrefs,
                                FullCardLinks = cardLinksValue
                            });
                        }
                        catch (Exception convEx)
                        {
                            perConverterResults.Add(new
                            {
                                ConverterType = converterProvider.GetType().FullName,
                                Error = $"{convEx.Message}\n{convEx.InnerException?.Message}"
                            });
                        }
                    }

                    perSiteResults.Add(new
                    {
                        SiteName = site.Name,
                        SiteUrl = site.SiteUrl?.ToString(),
                        ConverterResults = perConverterResults
                    });
                }

                SiteDefinition.Current = originalSite;

                return Ok(new
                {
                    Step = "6 — Serialize Test Block (per site context × per converter)",
                    TestBlockId = _testBlockId.ToString(),
                    TestBlockName = content.Name,
                    RegisteredConverterProviders = converterProviderTypes,
                    InjectedConverterProviderType = _contentConverterProvider.GetType().FullName,
                    PerSiteResults = perSiteResults,
                    Note = "Compare the CardLinks Href values across site contexts AND across converter providers. " +
                           "The Content Graph's ContentGraphContentConverterProvider may produce different URLs than the standard one."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 8: Build the real Content Graph JSON document for the test page or block.
        /// This uses IContentSerializer with the same ContentApiModelTransformer callback used by ContentIndexer.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/graph-serialize-test-content?target=page
        /// </summary>
        [HttpGet("graph-serialize-test-content")]
        public IActionResult GraphSerializeTestContent(
            [FromQuery] string target = "page",
            [FromQuery] bool transformed = true)
        {
            try
            {
                var content = ResolveTestContent(target);
                if (content == null)
                {
                    return BadRequest(new
                    {
                        Error = "Requested test content was not found.",
                        Target = target,
                        Hint = target?.Equals("block", StringComparison.OrdinalIgnoreCase) == true
                            ? "Call /create-test-block first."
                            : "Call /create-test-block then /create-site-b-test-page first."
                    });
                }

                var owningSite = _siteDefinitionResolver.GetByContent(content.ContentLink, true, true);
                var originalSite = SiteDefinition.Current;
                var allSites = _siteDefinitionRepository.List().ToList();
                var perCurrentSiteResults = new List<object>();

                foreach (var currentSite in allSites)
                {
                    SiteDefinition.Current = currentSite;

                    var json = BuildGraphJsonDocument(content, transformed);
                    var hrefs = ExtractJsonStringValues(json, "Href")
                        .Concat(ExtractJsonStringValues(json, "href"))
                        .Distinct()
                        .ToList();

                    perCurrentSiteResults.Add(new
                    {
                        CurrentSiteBeforeSerializer = new
                        {
                            currentSite.Name,
                            currentSite.Id,
                            SiteUrl = currentSite.SiteUrl?.ToString(),
                            StartPage = currentSite.StartPage?.ToString()
                        },
                        SerializerOwningSite = new
                        {
                            owningSite?.Name,
                            owningSite?.Id,
                            SiteUrl = owningSite?.SiteUrl?.ToString(),
                            StartPage = owningSite?.StartPage?.ToString()
                        },
                        Hrefs = hrefs,
                        ContainsFoundationHost = hrefs.Any(h => h.Contains("localhost:5000", StringComparison.OrdinalIgnoreCase)),
                        ContainsSiteBHost = hrefs.Any(h => h.Contains("site-b.local", StringComparison.OrdinalIgnoreCase)),
                        RawJson = TryParseJson(json)
                    });
                }

                SiteDefinition.Current = originalSite;

                return Ok(new
                {
                    Step = "8 - Real Content Graph Serializer",
                    Target = target,
                    TransformedLikeIndexer = transformed,
                    Content = new
                    {
                        content.Name,
                        ContentLink = content.ContentLink.ToString(),
                        content.ContentGuid,
                        ContentType = content.GetOriginalType().FullName
                    },
                    QueryOptions = new
                    {
                        _queryOptions.Value.SynchronizationEnabled,
                        _queryOptions.Value.PreventFieldCollision,
                        ExpandDefault = _queryOptions.Value.ExpandLevel.Default,
                        ExpandContentArea = _queryOptions.Value.ExpandLevel.ContentArea
                    },
                    PerCurrentSiteResults = perCurrentSiteResults,
                    Note = "This endpoint calls the same serializer method that ContentIndexer uses before sending documents to Content Graph."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 9: Send the test page or block through the real Content Graph indexer.
        /// This performs a real indexing request for one content item, not a simulation.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/index-test-content?target=page
        /// </summary>
        [HttpGet("index-test-content")]
        public async Task<IActionResult> IndexTestContent(
            [FromQuery] string target = "page",
            [FromQuery] bool throughEventHub = true)
        {
            try
            {
                var content = ResolveTestContent(target);
                if (content == null)
                {
                    return BadRequest(new
                    {
                        Error = "Requested test content was not found.",
                        Target = target,
                        Hint = target?.Equals("block", StringComparison.OrdinalIgnoreCase) == true
                            ? "Call /create-test-block first."
                            : "Call /create-test-block then /create-site-b-test-page first."
                    });
                }

                var owningSite = _siteDefinitionResolver.GetByContent(content.ContentLink, true, true);
                var response = await _contentIndexer.IndexAsync(content.ContentLink, throughEventHub);
                var serializedResponse = JsonConvert.SerializeObject(response);

                return Ok(new
                {
                    Step = "9 - Real Content Graph Indexer",
                    Target = target,
                    ThroughEventHub = throughEventHub,
                    Content = new
                    {
                        content.Name,
                        ContentLink = content.ContentLink.ToString(),
                        content.ContentGuid,
                        ContentType = content.GetOriginalType().FullName
                    },
                    OwningSite = new
                    {
                        owningSite?.Name,
                        owningSite?.Id,
                        SiteUrl = owningSite?.SiteUrl?.ToString(),
                        StartPage = owningSite?.StartPage?.ToString()
                    },
                    ContentGraph = new
                    {
                        _queryOptions.Value.GatewayAddress,
                        _queryOptions.Value.SynchronizationEnabled,
                        HasAppKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.AppKey),
                        HasSecret = !string.IsNullOrWhiteSpace(_queryOptions.Value.Secret)
                    },
                    Response = TryParseJson(serializedResponse),
                    Message = "The content item was sent through IContentIndexer.IndexAsync. Check the response and Graph/logs for the indexed document result."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 10: Apply the customer's workaround — use ISiteDefinitionResolver.GetByContent()
        /// to find the owning site, then rewrite the LinkItem host to the correct one.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/workaround-rewrite-host?nodeCode=mens
        /// </summary>
        [HttpGet("workaround-rewrite-host")]
        public IActionResult WorkaroundRewriteHost([FromQuery] string nodeCode = "mens")
        {
            try
            {
                var contentLink = _referenceConverter.GetContentLink(nodeCode);
                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return BadRequest(new { Error = $"No commerce content found for code '{nodeCode}'." });
                }

                var content = _contentLoader.Get<IContent>(contentLink);

                var permanentGuid = PermanentLinkUtility.FindGuid(contentLink);
                var permanentLinkUrl = PermanentLinkUtility.GetPermanentLinkVirtualPath(permanentGuid, ".aspx");
                var linkItem = new LinkItem { Href = permanentLinkUrl, Text = content.Name };

                var owningSite = _siteDefinitionResolver.GetByContent(contentLink, true, true);
                var owningSiteHost = owningSite?.Hosts?
                    .FirstOrDefault(h => h.Type == HostDefinitionType.Primary)?.Url
                    ?? owningSite?.SiteUrl;

                var originalSite = SiteDefinition.Current;
                var allSites = _siteDefinitionRepository.List().ToList();

                var perSiteResults = new List<object>();
                foreach (var site in allSites)
                {
                    SiteDefinition.Current = site;

                    var mappedHref = linkItem.GetMappedHref();
                    var siteHost = site.Hosts?
                        .FirstOrDefault(h => h.Type == HostDefinitionType.Primary)?.Url
                        ?? site.SiteUrl;

                    string wrongAbsoluteUrl = null;
                    if (siteHost != null && !string.IsNullOrEmpty(mappedHref))
                    {
                        Uri.TryCreate(siteHost, mappedHref, out var wrongUri);
                        wrongAbsoluteUrl = wrongUri?.ToString();
                    }

                    string correctedUrl = wrongAbsoluteUrl;
                    bool wasRewritten = false;

                    if (owningSiteHost != null && !string.IsNullOrEmpty(wrongAbsoluteUrl))
                    {
                        if (Uri.TryCreate(wrongAbsoluteUrl, UriKind.Absolute, out var hrefUri))
                        {
                            var builder = new UriBuilder(hrefUri)
                            {
                                Scheme = owningSiteHost.Scheme,
                                Host = owningSiteHost.Host,
                                Port = owningSiteHost.IsDefaultPort ? -1 : owningSiteHost.Port
                            };
                            correctedUrl = builder.Uri.AbsoluteUri;
                            wasRewritten = hrefUri.Host != owningSiteHost.Host;
                        }
                    }

                    perSiteResults.Add(new
                    {
                        SiteName = site.Name,
                        IsOwningSite = owningSite?.Id == site.Id,
                        WrongUrl = wrongAbsoluteUrl,
                        CorrectedUrl = correctedUrl,
                        WasRewritten = wasRewritten,
                        HostFixed = wasRewritten
                            ? $"Rewrote host from '{siteHost?.Host}' to '{owningSiteHost?.Host}'"
                            : "Host already correct — no rewrite needed"
                    });
                }

                SiteDefinition.Current = originalSite;

                return Ok(new
                {
                    Step = "10 - Workaround: Rewrite Host via ISiteDefinitionResolver",
                    NodeCode = nodeCode,
                    ContentName = content.Name,
                    ContentLink = contentLink.ToString(),
                    OwningSite = new
                    {
                        owningSite?.Name,
                        owningSite?.Id,
                        SiteUrl = owningSite?.SiteUrl?.ToString(),
                        PrimaryHost = owningSiteHost?.ToString()
                    },
                    PerSiteResults = perSiteResults,
                    WorkaroundCode = new
                    {
                        Description = "ContentApiModelFilter implementation pattern",
                        PseudoCode = new[]
                        {
                            "var site = siteDefinitionResolver.GetByContent(contentReference, true, true);",
                            "var primaryUrl = site.Hosts.FirstOrDefault(h => h.Type == HostDefinitionType.Primary)?.Url ?? site.SiteUrl;",
                            "var builder = new UriBuilder(hrefUri) { Scheme = primaryUrl.Scheme, Host = primaryUrl.Host, Port = primaryUrl.IsDefaultPort ? -1 : primaryUrl.Port };",
                            "linkItem.Href = builder.Uri.AbsoluteUri;"
                        },
                        Note = "Apply only to commerce-backed LinkItem values. Non-commerce LinkItems do not exhibit this issue."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private IContent ResolveTestContent(string target)
        {
            var contentLink = target?.Equals("block", StringComparison.OrdinalIgnoreCase) == true
                ? _testBlockId
                : _testPageId;

            if (ContentReference.IsNullOrEmpty(contentLink))
            {
                return null;
            }

            return _contentLoader.Get<IContent>(contentLink);
        }

        private string BuildGraphJsonDocument(IContent content, bool transformed)
        {
            if (!transformed)
            {
                return _contentSerializer.CreateJsonContent(content, null);
            }

            using var scope = new ContentIndexingScope(true);
            var contentType = _contentTypeRepository.Load(content.ContentTypeID);
            return _contentSerializer.CreateJsonContent(content, contentApiModel =>
                _contentApiModelTransformer.Transform(scope, null, contentType, contentApiModel));
        }

        private static List<string> ExtractJsonStringValues(string json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            {
                return new List<string>();
            }

            var values = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(json);
                CollectJsonStringValues(document.RootElement, propertyName, values);
            }
            catch
            {
                // Keep this helper non-fatal; the endpoint returns the raw parse error separately.
            }

            return values;
        }

        private static void CollectJsonStringValues(JsonElement element, string propertyName, List<string> values)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if ((property.NameEquals(propertyName) ||
                             property.Name.Contains(propertyName, StringComparison.OrdinalIgnoreCase)) &&
                            property.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                values.Add(value);
                            }
                        }

                        CollectJsonStringValues(property.Value, propertyName, values);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        CollectJsonStringValues(item, propertyName, values);
                    }
                    break;
            }
        }

        private static object TryParseJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new
                {
                    IsEmpty = true,
                    Raw = rawJson
                };
            }

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                return new
                {
                    IsJson = false,
                    Error = ex.Message,
                    Raw = rawJson
                };
            }
        }

        /// <summary>
        /// Step 11: Remove the second site definition and test content created in earlier steps.
        /// Sample usage: https://localhost:5009/util-api/custom-link-item-host/cleanup
        /// </summary>
        [HttpGet("cleanup")]
        public IActionResult Cleanup()
        {
            try
            {
                var results = new List<string>();

                // Clean up test page before deleting the block it references
                if (!ContentReference.IsNullOrEmpty(_testPageId))
                {
                    try
                    {
                        _contentRepository.Delete(_testPageId, true, AccessLevel.NoAccess);
                        results.Add($"Deleted test page {_testPageId}");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"Failed to delete test page: {ex.Message}");
                    }
                    _testPageId = ContentReference.EmptyReference;
                }

                // Clean up test block
                if (!ContentReference.IsNullOrEmpty(_testBlockId))
                {
                    try
                    {
                        _contentRepository.Delete(_testBlockId, true, AccessLevel.NoAccess);
                        results.Add($"Deleted test block {_testBlockId}");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"Failed to delete test block: {ex.Message}");
                    }
                    _testBlockId = ContentReference.EmptyReference;
                }

                foreach (var lingeringBlockRef in _contentLoader.GetDescendents(ContentReference.GlobalBlockFolder).ToList())
                {
                    try
                    {
                        var lingeringBlock = _contentLoader.Get<IContent>(lingeringBlockRef);
                        if (lingeringBlock.Name.StartsWith("LinkItem-Host-Test-", StringComparison.OrdinalIgnoreCase))
                        {
                            _contentRepository.Delete(lingeringBlock.ContentLink, true, AccessLevel.NoAccess);
                            results.Add($"Deleted lingering test block {lingeringBlock.ContentLink}");
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add($"Failed to inspect/delete lingering test block {lingeringBlockRef}: {ex.Message}");
                    }
                }

                // Clean up second site
                if (_createdSiteId != Guid.Empty)
                {
                    var site = _siteDefinitionRepository.Get(_createdSiteId);
                    if (site != null)
                    {
                        if (ContentReference.IsNullOrEmpty(_siteBStartPageId))
                        {
                            _siteBStartPageId = site.StartPage ?? ContentReference.EmptyReference;
                        }

                        _siteDefinitionRepository.Delete(site.Id);
                        results.Add($"Deleted site '{site.Name}'");
                    }
                    _createdSiteId = Guid.Empty;
                }

                // Also try to delete any lingering site-b from previous runs
                var siteB = _siteDefinitionRepository.List().FirstOrDefault(s => s.Name == "site-b");
                if (siteB != null)
                {
                    if (ContentReference.IsNullOrEmpty(_siteBStartPageId))
                    {
                        _siteBStartPageId = siteB.StartPage ?? ContentReference.EmptyReference;
                    }

                    _siteDefinitionRepository.Delete(siteB.Id);
                    results.Add($"Deleted lingering site '{siteB.Name}'");
                }

                // Clean up site-b start page (must happen after site deletion)
                if (!ContentReference.IsNullOrEmpty(_siteBStartPageId))
                {
                    try
                    {
                        _contentRepository.Delete(_siteBStartPageId, true, AccessLevel.NoAccess);
                        results.Add($"Deleted site-b start page {_siteBStartPageId}");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"Failed to delete site-b start page: {ex.Message}");
                    }
                    _siteBStartPageId = ContentReference.EmptyReference;
                }

                var remaining = _siteDefinitionRepository.List()
                    .Select(s => new { s.Name, s.Id, SiteUrl = s.SiteUrl?.ToString() })
                    .ToList();

                return Ok(new
                {
                    Step = "11 - Cleanup",
                    Actions = results,
                    RemainingSites = remaining
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }
    }
}
