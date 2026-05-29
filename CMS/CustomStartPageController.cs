using EPiServer.Globalization;
using Foundation.Features.Home;
using Foundation.Infrastructure.Cms.Settings;

namespace Foundation.Custom.Episerver_util_api.CMS
{
    /// <summary>
    /// Diagnostic API for comparing a per-request StartPage lookup with a long-lived Lazy StartPage lookup.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-start-page")]
    public class CustomStartPageController : ControllerBase
    {
        private static readonly object LazyLock = new object();
        private static Lazy<StartPageSnapshot> _lazyStartPage;

        private readonly IContentLoader _contentLoader;
        private readonly ISiteDefinitionRepository _siteDefinitionRepository;
        private readonly ISettingsService _settingsService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CustomStartPageController(
            IContentLoader contentLoader,
            ISiteDefinitionRepository siteDefinitionRepository,
            ISettingsService settingsService,
            IHttpContextAccessor httpContextAccessor)
        {
            _contentLoader = contentLoader;
            _siteDefinitionRepository = siteDefinitionRepository;
            _settingsService = settingsService;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>
        /// Step 1: Shows the available diagnostic steps and sample browser URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/summary
        /// </summary>
        [HttpGet("summary")]
        public IActionResult Summary()
        {
            try
            {
                return Ok(new
                {
                    Purpose = "Compare fresh StartPage resolution with a long-lived Lazy StartPage resolution.",
                    Steps = new[]
                    {
                        "Step 1: GET /util-api/custom-start-page/summary",
                        "Step 2: GET /util-api/custom-start-page/current-context",
                        "Step 3: GET /util-api/custom-start-page/fresh-start-page",
                        "Step 4: GET /util-api/custom-start-page/lazy-start-page",
                        "Step 5: GET /util-api/custom-start-page/compare",
                        "Step 6: GET /util-api/custom-start-page/reference-settings",
                        "Optional: GET /util-api/custom-start-page/reset-lazy"
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-start-page/current-context",
                        "https://localhost:5009/util-api/custom-start-page/fresh-start-page",
                        "https://localhost:5009/util-api/custom-start-page/lazy-start-page",
                        "https://localhost:5009/util-api/custom-start-page/compare",
                        "https://localhost:5009/util-api/custom-start-page/reference-settings",
                        "https://localhost:5009/util-api/custom-start-page/reset-lazy"
                    },
                    HowToReproduce = new[]
                    {
                        "Open lazy-start-page first on one host/site to initialize the static Lazy value.",
                        "Open compare on another host/site or language.",
                        "If LazyStartPage differs from FreshStartPage, a singleton Lazy StartPage cache can serve settings for the wrong site context."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Shows the current request host, language, SiteDefinition.Current, and configured sites.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/current-context
        /// </summary>
        [HttpGet("current-context")]
        public IActionResult CurrentContext()
        {
            try
            {
                var currentSite = SiteDefinition.Current;
                var request = _httpContextAccessor.HttpContext?.Request;

                return Ok(new
                {
                    Request = new
                    {
                        Host = request?.Host.ToString(),
                        Scheme = request?.Scheme,
                        Path = request?.Path.ToString(),
                        QueryString = request?.QueryString.ToString()
                    },
                    Culture = GetCultureSnapshot(),
                    CurrentSite = GetSiteSnapshot(currentSite),
                    ContentReferenceStartPage = GetContentReferenceSnapshot(ContentReference.StartPage),
                    ConfiguredSites = _siteDefinitionRepository.List()
                        .Select(GetSiteSnapshot)
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Resolves the StartPage fresh for this request, using SiteDefinition.Current.StartPage and an optional first-site fallback.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/fresh-start-page
        /// </summary>
        [HttpGet("fresh-start-page")]
        public IActionResult FreshStartPage(bool fallbackToFirstSite = true)
        {
            try
            {
                var snapshot = ResolveStartPageSnapshot("Fresh per-request lookup", fallbackToFirstSite);

                return Ok(new
                {
                    Lookup = snapshot,
                    Note = "This endpoint performs the StartPage resolution during each request."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Resolves the StartPage through a static Lazy value that is initialized only once per app process.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/lazy-start-page
        /// </summary>
        [HttpGet("lazy-start-page")]
        public IActionResult LazyStartPage()
        {
            try
            {
                var lazyStartPage = GetLazyStartPage();
                var lazySnapshot = lazyStartPage.Value;
                var freshSnapshot = ResolveStartPageSnapshot("Fresh per-request lookup", true);

                return Ok(new
                {
                    LazyStartPage = lazySnapshot,
                    FreshStartPageNow = freshSnapshot,
                    LazyAlreadyInitialized = lazyStartPage.IsValueCreated,
                    SameStartPage = lazySnapshot.ContentLink?.Id == freshSnapshot.ContentLink?.Id,
                    Note = "This mimics a long-lived Lazy<StartPage>; the first request that touches this endpoint fixes the lazy value until reset or process restart."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Compares the current fresh StartPage against the cached Lazy StartPage.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/compare
        /// </summary>
        [HttpGet("compare")]
        public IActionResult Compare()
        {
            try
            {
                var lazySnapshot = GetLazyStartPage().Value;
                var freshSnapshot = ResolveStartPageSnapshot("Fresh per-request lookup", true);
                var sameStartPage = lazySnapshot.ContentLink?.Id == freshSnapshot.ContentLink?.Id;
                var sameSite = string.Equals(lazySnapshot.Site?.Id, freshSnapshot.Site?.Id, StringComparison.OrdinalIgnoreCase);
                var sameCulture = string.Equals(lazySnapshot.Culture?.PreferredCulture, freshSnapshot.Culture?.PreferredCulture, StringComparison.OrdinalIgnoreCase);

                return Ok(new
                {
                    SameStartPage = sameStartPage,
                    SameSite = sameSite,
                    SameCulture = sameCulture,
                    RiskDetected = !sameStartPage || !sameSite || !sameCulture,
                    LazyStartPage = lazySnapshot,
                    FreshStartPage = freshSnapshot,
                    Interpretation = sameStartPage && sameSite && sameCulture
                        ? "The lazy StartPage currently matches the fresh request context."
                        : "The lazy StartPage does not fully match the fresh request context. A singleton Lazy StartPage lookup can read settings from the wrong site or language context."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Reads a common site settings object for the current request, or for a specific site ID.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/reference-settings
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/reference-settings?siteId=00000000-0000-0000-0000-000000000000
        /// </summary>
        [HttpGet("reference-settings")]
        public IActionResult ReferenceSettings(string siteId = null)
        {
            try
            {
                Guid? parsedSiteId = null;
                if (!string.IsNullOrWhiteSpace(siteId))
                {
                    parsedSiteId = Guid.Parse(siteId);
                }

                var settings = _settingsService.GetSiteSettings<ReferencePageSettings>(parsedSiteId);

                return Ok(new
                {
                    RequestedSiteId = parsedSiteId?.ToString(),
                    CurrentContext = new
                    {
                        Site = GetSiteSnapshot(SiteDefinition.Current),
                        Culture = GetCultureSnapshot()
                    },
                    SettingsFound = settings != null,
                    SettingsType = typeof(ReferencePageSettings).FullName,
                    SettingsCacheKeys = _settingsService.SiteSettings.Keys.OrderBy(x => x).ToList(),
                    Values = settings == null ? null : new
                    {
                        SearchPage = GetContentReferenceSnapshot(settings.SearchPage),
                        WishlistPage = GetContentReferenceSnapshot(settings.WishlistPage),
                        CartPage = GetContentReferenceSnapshot(settings.CartPage),
                        CheckoutPage = GetContentReferenceSnapshot(settings.CheckoutPage),
                        OrderHistoryPage = GetContentReferenceSnapshot(settings.OrderHistoryPage)
                    },
                    Note = "Use this step to see whether settings resolution follows the current request site and language."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Optional step: Resets the static Lazy StartPage so the next lazy-start-page request initializes it again.
        /// Sample usage: https://localhost:5009/util-api/custom-start-page/reset-lazy
        /// </summary>
        [HttpGet("reset-lazy")]
        public IActionResult ResetLazy()
        {
            try
            {
                lock (LazyLock)
                {
                    _lazyStartPage = null;
                }

                return Ok(new
                {
                    Reset = true,
                    LazyAlreadyInitialized = false,
                    NextStep = "Open https://localhost:5009/util-api/custom-start-page/lazy-start-page to initialize the Lazy value again."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private Lazy<StartPageSnapshot> GetLazyStartPage()
        {
            if (_lazyStartPage != null)
            {
                return _lazyStartPage;
            }

            lock (LazyLock)
            {
                _lazyStartPage ??= new Lazy<StartPageSnapshot>(() => ResolveStartPageSnapshot("Static Lazy lookup", true));
                return _lazyStartPage;
            }
        }

        private StartPageSnapshot ResolveStartPageSnapshot(string source, bool fallbackToFirstSite)
        {
            var currentSite = SiteDefinition.Current;
            var startPageReference = currentSite?.StartPage;
            var usedFallback = false;

            if (IsEmpty(startPageReference) && fallbackToFirstSite)
            {
                var firstSite = _siteDefinitionRepository.List().FirstOrDefault();
                currentSite = firstSite;
                startPageReference = firstSite?.StartPage;
                usedFallback = true;
            }

            IContent content = null;
            var loadSucceeded = false;

            if (!IsEmpty(startPageReference))
            {
                loadSucceeded = _contentLoader.TryGet(startPageReference, out content);
            }

            return new StartPageSnapshot
            {
                Source = source,
                ResolvedAtUtc = DateTime.UtcNow,
                UsedFallbackToFirstSite = usedFallback,
                Site = GetSiteSnapshot(currentSite),
                Culture = GetCultureSnapshot(),
                ContentLink = GetContentReferenceSnapshot(content?.ContentLink ?? startPageReference),
                Content = GetContentSnapshot(content),
                LoadSucceeded = loadSucceeded
            };
        }

        private static bool IsEmpty(ContentReference reference)
        {
            return reference == null || ContentReference.IsNullOrEmpty(reference) || reference.ID == 0;
        }

        private static CultureSnapshot GetCultureSnapshot()
        {
            return new CultureSnapshot
            {
                PreferredCulture = ContentLanguage.PreferredCulture?.Name,
                SystemCulture = System.Globalization.CultureInfo.CurrentCulture.Name,
                SystemUiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name
            };
        }

        private static SiteSnapshot GetSiteSnapshot(SiteDefinition site)
        {
            if (site == null)
            {
                return null;
            }

            return new SiteSnapshot
            {
                Name = site.Name,
                Id = site.Id.ToString(),
                SiteUrl = site.SiteUrl?.ToString(),
                StartPage = GetContentReferenceSnapshot(site.StartPage),
                Hosts = site.Hosts?.Select(x => new
                {
                    Name = x.Name?.ToString(),
                    x.Type,
                    Language = x.Language?.Name
                }).ToList()
            };
        }

        private static ContentReferenceSnapshot GetContentReferenceSnapshot(ContentReference contentReference)
        {
            if (IsEmpty(contentReference))
            {
                return null;
            }

            return new ContentReferenceSnapshot
            {
                Id = contentReference.ID,
                WorkId = contentReference.WorkID,
                ProviderName = contentReference.ProviderName,
                Value = contentReference.ToString()
            };
        }

        private static ContentSnapshot GetContentSnapshot(IContent content)
        {
            if (content == null)
            {
                return null;
            }

            return new ContentSnapshot
            {
                Name = content.Name,
                Type = content.GetOriginalType().FullName,
                IsHomePage = content is HomePage,
                ContentGuid = content.ContentGuid,
                ContentLink = GetContentReferenceSnapshot(content.ContentLink)
            };
        }

        private class StartPageSnapshot
        {
            public string Source { get; set; }
            public DateTime ResolvedAtUtc { get; set; }
            public bool UsedFallbackToFirstSite { get; set; }
            public SiteSnapshot Site { get; set; }
            public CultureSnapshot Culture { get; set; }
            public ContentReferenceSnapshot ContentLink { get; set; }
            public ContentSnapshot Content { get; set; }
            public bool LoadSucceeded { get; set; }
        }

        private class SiteSnapshot
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string SiteUrl { get; set; }
            public ContentReferenceSnapshot StartPage { get; set; }
            public object Hosts { get; set; }
        }

        private class CultureSnapshot
        {
            public string PreferredCulture { get; set; }
            public string SystemCulture { get; set; }
            public string SystemUiCulture { get; set; }
        }

        private class ContentReferenceSnapshot
        {
            public int Id { get; set; }
            public int WorkId { get; set; }
            public string ProviderName { get; set; }
            public string Value { get; set; }
        }

        private class ContentSnapshot
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsHomePage { get; set; }
            public Guid ContentGuid { get; set; }
            public ContentReferenceSnapshot ContentLink { get; set; }
        }
    }
}
