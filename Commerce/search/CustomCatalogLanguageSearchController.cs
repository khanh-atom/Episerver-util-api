using EPiServer.Find;
using EPiServer.Find.Cms;
using EPiServer.Find.Cms.SearchProviders;
using EPiServer.Find.Commerce;
using EPiServer.Globalization;
using EPiServer.Shell.Search;
using Mediachase.Commerce.Core;
using Mediachase.Search;
using Mediachase.Search.Extensions;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.Search
{
    /// <summary>
    /// API controller to replicate and debug the issue where commerce predictive search
    /// in the CMS editor only returns results in a single language (e.g. en-US) after a package upgrade.
    /// Walks through each layer: search providers, Find query, culture filter, and content loading.
    /// Route: util-api/custom-catalog-language-search
    /// </summary>
    [ApiController]
    [Route("util-api/custom-catalog-language-search")]
    public class CustomCatalogLanguageSearchController : ControllerBase
    {
        private readonly IEnumerable<ISearchProvider> _searchProviders;
        private readonly IClient _findClient;
        private readonly IContentLoader _contentLoader;
        private readonly IContentLanguageAccessor _languageAccessor;
        private readonly ILanguageBranchRepository _languageBranchRepository;
        private readonly ServiceAccessor<SearchManager> _searchManagerAccessor;
        private readonly IOptions<SearchOptions> _searchOptions;
        private readonly ServiceAccessor<SiteContext> _siteContextAccessor;

        public CustomCatalogLanguageSearchController(
            IEnumerable<ISearchProvider> searchProviders,
            IClient findClient,
            IContentLoader contentLoader,
            IContentLanguageAccessor languageAccessor,
            ILanguageBranchRepository languageBranchRepository,
            ServiceAccessor<SearchManager> searchManagerAccessor,
            IOptions<SearchOptions> searchOptions,
            ServiceAccessor<SiteContext> siteContextAccessor)
        {
            _searchProviders = searchProviders;
            _findClient = findClient;
            _contentLoader = contentLoader;
            _languageAccessor = languageAccessor;
            _languageBranchRepository = languageBranchRepository;
            _searchManagerAccessor = searchManagerAccessor;
            _searchOptions = searchOptions;
            _siteContextAccessor = siteContextAccessor;
        }

        /// <summary>
        /// Step 1: Show the current language context and all enabled languages.
        /// Demonstrates what _languageResolver.Language returns — this is the single culture
        /// used by ProductSearchProviderBase to load content, which limits results to one language.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/language-context
        /// </summary>
        [HttpGet("language-context")]
        public IActionResult LanguageContext()
        {
            try
            {
                var currentLanguage = _languageAccessor.Language;
                var enabledLanguages = _languageBranchRepository.ListEnabled();

                return Ok(new
                {
                    Description = "Step 1: Shows the current language context. ProductSearchProviderBase uses _languageResolver.Language (a single culture) to load content, limiting results to only this language.",
                    CurrentLanguage = new
                    {
                        Name = currentLanguage?.Name,
                        DisplayName = currentLanguage?.DisplayName,
                        EnglishName = currentLanguage?.EnglishName
                    },
                    PreferredCulture = new
                    {
                        Name = ContentLanguage.PreferredCulture?.Name,
                        DisplayName = ContentLanguage.PreferredCulture?.DisplayName
                    },
                    EnabledLanguages = enabledLanguages.Select(lb => new
                    {
                        lb.LanguageID,
                        lb.Name,
                        lb.Enabled,
                        lb.URLSegment
                    }).ToList(),
                    Impact = "ProductSearchProviderBase calls _contentLoader.GetItems(keys, culture) with only the current language. All other language versions are discarded."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: List all registered catalog search providers and identify which one is active.
        /// When both Commerce and Find.Commerce are installed, two providers register for "Commerce/Catalog".
        /// The active provider determines whether multi-language results are returned.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/list-catalog-providers
        /// </summary>
        [HttpGet("list-catalog-providers")]
        public IActionResult ListCatalogProviders()
        {
            try
            {
                var allProviders = _searchProviders.ToList();
                var catalogProviders = allProviders
                    .Where(p => p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return Ok(new
                {
                    Description = "Step 2: Lists all search providers for Commerce/Catalog. Two providers may coexist: ProductSearchProviderBase (Lucene, single language) and EnterpriseCatalogSearchProvider (Find, all languages).",
                    CatalogProviderCount = catalogProviders.Count,
                    CatalogProviders = catalogProviders.Select(p => new
                    {
                        Type = p.GetType().FullName,
                        Assembly = p.GetType().Assembly.GetName().Name,
                        Area = p.Area,
                        Category = p.Category,
                        SortOrder = p.GetType().GetProperty("SortOrder")?.GetValue(p) as int?,
                        IsFind = p.GetType().FullName?.Contains("Enterprise") == true
                                 || p.GetType().FullName?.Contains("Find") == true,
                        LanguageBehavior = p.GetType().FullName?.Contains("Enterprise") == true
                            ? "Returns ALL language versions (queries Find index per-language document)"
                            : "Returns CURRENT language only (_contentLoader.GetItems with single culture)"
                    }).ToList(),
                    AllProviderAreas = allProviders
                        .Select(p => new { Type = p.GetType().Name, p.Area })
                        .OrderBy(p => p.Area)
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Search via the default ProductSearchProvider (Lucene-based).
        /// This provider only loads content in the current language via _contentLoader.GetItems(keys, culture).
        /// Demonstrates the single-language limitation.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/search-default-provider?keyword=ANDREA
        /// </summary>
        [HttpGet("search-default-provider")]
        public IActionResult SearchDefaultProvider([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var provider = _searchProviders.FirstOrDefault(p =>
                    p.GetType().Name.Contains("ProtectedProductSearchProvider")
                    || p.GetType().Name.Contains("ProductSearchProvider"));

                if (provider == null)
                {
                    provider = _searchProviders.FirstOrDefault(p =>
                        p.Area.Equals("Commerce/Catalog", StringComparison.OrdinalIgnoreCase)
                        && !p.GetType().FullName.Contains("Enterprise")
                        && !p.GetType().FullName.Contains("Improved"));
                }

                if (provider == null)
                {
                    return Ok(new
                    {
                        Description = "Step 3: No default Commerce ProductSearchProvider found.",
                        AvailableProviders = _searchProviders.Select(p => new { p.GetType().FullName, p.Area }).ToList()
                    });
                }

                var query = new Query(keyword, 20);
                var results = provider.Search(query)?.ToList() ?? new List<SearchResult>();

                return Ok(new
                {
                    Description = "Step 3: Searches via default ProductSearchProvider. This provider loads content in the CURRENT language only.",
                    ProviderType = provider.GetType().FullName,
                    Keyword = keyword,
                    CurrentLanguage = _languageAccessor.Language?.Name,
                    ResultCount = results.Count,
                    Results = results.Take(20).Select(sr => new
                    {
                        sr.Title,
                        sr.PreviewText,
                        sr.Language,
                        LanguageBranch = sr.Metadata.TryGetValue("LanguageBranch", out var lb) ? lb : null,
                        Id = sr.Metadata.TryGetValue("Id", out var id) ? id : null,
                        Url = sr.Url
                    }).ToList(),
                    DistinctLanguages = results.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                    Explanation = "Notice all results are in the same language. ProductSearchProviderBase.CreateSearchResults loads content via _contentLoader.GetItems(lookup.Keys, culture) where culture = _languageResolver.Language."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Search via Find-based EnterpriseCatalogSearchProvider.
        /// This provider queries the Find index which has per-language documents and loads each result
        /// in its indexed language. Should return results in ALL language versions.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/search-find-provider?keyword=ANDREA
        /// </summary>
        [HttpGet("search-find-provider")]
        public IActionResult SearchFindProvider([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var provider = _searchProviders.FirstOrDefault(p =>
                    p.GetType().Name.Contains("EnterpriseCatalogSearchProvider")
                    || p.GetType().Name.Contains("ImprovedEnterprise"));

                if (provider == null)
                {
                    return Ok(new
                    {
                        Description = "Step 4: No Find-based EnterpriseCatalogSearchProvider found. This means the Find.Commerce package may not be installed or the provider is disabled.",
                        AvailableProviders = _searchProviders
                            .Where(p => p.Area.StartsWith("Commerce", StringComparison.OrdinalIgnoreCase))
                            .Select(p => new { p.GetType().FullName, p.Area }).ToList(),
                        Troubleshooting = "Check that EPiServer.Find.Commerce NuGet package is installed and the provider is enabled in Admin > Search Configuration."
                    });
                }

                var query = new Query(keyword, 20) { FilterOnCulture = false };
                var results = provider.Search(query)?.ToList() ?? new List<SearchResult>();

                return Ok(new
                {
                    Description = "Step 4: Searches via Find-based EnterpriseCatalogSearchProvider. This should return results in ALL language versions.",
                    ProviderType = provider.GetType().FullName,
                    Keyword = keyword,
                    FilterOnCulture = false,
                    ResultCount = results.Count,
                    Results = results.Take(20).Select(sr => new
                    {
                        sr.Title,
                        sr.PreviewText,
                        sr.Language,
                        LanguageBranch = sr.Metadata.TryGetValue("LanguageBranch", out var lb) ? lb : null,
                        Id = sr.Metadata.TryGetValue("Id", out var id) ? id : null,
                        Url = sr.Url
                    }).ToList(),
                    DistinctLanguages = results.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                    Explanation = "If only one language appears, check: (1) Find index needs re-indexing, (2) EnableLanguageRoutingInSearchRequest is true, (3) FilterOnCulture is being set to true somewhere."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Compare FilterOnCulture=true vs false on the Find-based provider.
        /// When FilterOnCulture=true, DefaultCultureFilter restricts to the current language.
        /// When FilterOnCulture=false (default for catalog search), all languages are returned.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/compare-culture-filter?keyword=ANDREA
        /// </summary>
        [HttpGet("compare-culture-filter")]
        public IActionResult CompareCultureFilter([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var provider = _searchProviders.FirstOrDefault(p =>
                    p.GetType().Name.Contains("EnterpriseCatalogSearchProvider")
                    || p.GetType().Name.Contains("ImprovedEnterprise"));

                if (provider == null)
                {
                    return Ok(new
                    {
                        Description = "Step 5: No Find-based provider found.",
                        Hint = "Install EPiServer.Find.Commerce or check Search Configuration."
                    });
                }

                // FilterOnCulture = true (restricts to current language)
                var queryFiltered = new Query(keyword, 20) { FilterOnCulture = true };
                List<SearchResult> filteredResults;
                string filteredError = null;
                try
                {
                    filteredResults = provider.Search(queryFiltered)?.ToList() ?? new List<SearchResult>();
                }
                catch (Exception ex)
                {
                    filteredResults = new List<SearchResult>();
                    filteredError = $"{ex.GetType().Name}: {ex.Message}";
                }

                // FilterOnCulture = false (returns all languages)
                var queryUnfiltered = new Query(keyword, 20) { FilterOnCulture = false };
                List<SearchResult> unfilteredResults;
                string unfilteredError = null;
                try
                {
                    unfilteredResults = provider.Search(queryUnfiltered)?.ToList() ?? new List<SearchResult>();
                }
                catch (Exception ex)
                {
                    unfilteredResults = new List<SearchResult>();
                    unfilteredError = $"{ex.GetType().Name}: {ex.Message}";
                }

                return Ok(new
                {
                    Description = "Step 5: Compares FilterOnCulture=true (single language) vs false (all languages). DefaultCultureFilter in FilterFactory checks this flag.",
                    ProviderType = provider.GetType().FullName,
                    Keyword = keyword,
                    CurrentLanguage = _languageAccessor.Language?.Name,
                    FilteredByCulture = new
                    {
                        FilterOnCulture = true,
                        ResultCount = filteredResults.Count,
                        Error = filteredError,
                        DistinctLanguages = filteredResults.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                        Results = filteredResults.Take(10).Select(sr => new { sr.Title, sr.Language }).ToList()
                    },
                    NotFilteredByCulture = new
                    {
                        FilterOnCulture = false,
                        ResultCount = unfilteredResults.Count,
                        Error = unfilteredError,
                        DistinctLanguages = unfilteredResults.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                        Results = unfilteredResults.Take(10).Select(sr => new { sr.Title, sr.Language }).ToList()
                    },
                    Explanation = "FilterFactory.DefaultCultureFilter checks query.FilterOnCulture. When true: searchRequest.InLanguageBranch(preferredCulture). When false: returns all languages."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Query Find index directly to check what language versions are indexed.
        /// Bypasses all search providers and queries Elasticsearch directly to verify
        /// that catalog content exists in the index for each enabled language.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/check-find-index?keyword=ANDREA
        /// </summary>
        [HttpGet("check-find-index")]
        public IActionResult CheckFindIndex([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var enabledLanguages = _languageBranchRepository.ListEnabled()
                    .Select(lb => lb.LanguageID).ToList();

                var perLanguageResults = new List<object>();

                foreach (var lang in enabledLanguages)
                {
                    int count = 0;
                    string error = null;
                    List<object> items = new();
                    try
                    {
                        var culture = new CultureInfo(lang);
                        var search = _findClient.Search<CatalogContentBase>()
                            .For(keyword)
                            .InField(x => x.SearchText())
                            .Filter(x => x.MatchTypeHierarchy(typeof(EntryContentBase)))
                            .FilterOnLanguages(new[] { lang })
                            .Take(10);

                        var results = search.GetContentResult();
                        count = results.TotalMatching;
                        items = results.Take(5).Select(c => new
                        {
                            Name = (c as IContent)?.Name,
                            Code = (c as EntryContentBase)?.Code,
                            ContentLink = (c as IContent)?.ContentLink?.ToString(),
                            Language = (c as ILocalizable)?.Language?.Name,
                            ExistingLanguages = (c as ILocalizable)?.ExistingLanguages?.Select(el => el.Name).ToList()
                        }).Cast<object>().ToList();
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    perLanguageResults.Add(new
                    {
                        Language = lang,
                        IndexedCount = count,
                        Error = error,
                        SampleItems = items
                    });
                }

                return Ok(new
                {
                    Description = "Step 6: Queries Find index directly per language to verify that catalog content is indexed in each enabled language.",
                    Keyword = keyword,
                    EnabledLanguages = enabledLanguages,
                    PerLanguageResults = perLanguageResults,
                    Troubleshooting = "If a language shows 0 results, run the 'Search & Navigation Content Indexing Job' from CMS Admin > Scheduled Jobs to rebuild the Find index."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Simulate the full composite search flow as SearchResultStore does.
        /// Calls ALL catalog providers, merges results, and shows language distribution.
        /// This replicates the exact flow triggered by the CMS editor search box.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/composite-search?keyword=ANDREA
        /// </summary>
        [HttpGet("composite-search")]
        public IActionResult CompositeSearch([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var catalogProviders = _searchProviders
                    .Where(p => p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var query = new Query(keyword, 20) { FilterOnCulture = false };
                var perProviderResults = new List<object>();
                var allResults = new List<SearchResult>();

                foreach (var provider in catalogProviders)
                {
                    List<SearchResult> providerResults = null;
                    string error = null;
                    try
                    {
                        providerResults = provider.Search(query)?.ToList();
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    if (providerResults != null)
                        allResults.AddRange(providerResults);

                    perProviderResults.Add(new
                    {
                        ProviderType = provider.GetType().FullName,
                        Category = provider.Category,
                        SearchError = error,
                        IsResultNull = providerResults == null,
                        ResultCount = providerResults?.Count ?? 0,
                        DistinctLanguages = providerResults?.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                        Results = providerResults?.Take(10).Select(sr => new
                        {
                            sr.Title,
                            sr.Language,
                            Id = sr.Metadata.TryGetValue("Id", out var id) ? id : null,
                            LanguageBranch = sr.Metadata.TryGetValue("LanguageBranch", out var lb) ? lb : null
                        }).ToList()
                    });
                }

                return Ok(new
                {
                    Description = "Step 7: Simulates the full composite search flow as triggered by the CMS editor search box. Calls all catalog providers and merges results.",
                    Keyword = keyword,
                    CurrentLanguage = _languageAccessor.Language?.Name,
                    ProvidersSearched = catalogProviders.Count,
                    TotalMergedResults = allResults.Count,
                    MergedDistinctLanguages = allResults.Select(r => r.Language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
                    PerProviderResults = perProviderResults,
                    Diagnosis = allResults.Select(r => r.Language).Distinct().Count() <= 1
                        ? "ISSUE CONFIRMED: Only one language in results. Check: (1) Is the Find-based provider active? (2) Is the Find index populated for all languages? (3) Is EnableLanguageRoutingInSearchRequest=true restricting the query?"
                        : "Multiple languages found in results. The search is working as expected."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 8: Demonstrate the fix — load content in ALL languages for a given catalog entry code.
        /// Shows what ProductSearchProviderBase should do: load all language branches instead of
        /// loading content in only the current language.
        /// Sample: https://localhost:5009/util-api/custom-catalog-language-search/load-all-languages?code=P-40707713
        /// </summary>
        [HttpGet("load-all-languages")]
        public IActionResult LoadAllLanguages([FromQuery] string code = "P-40707713")
        {
            try
            {
                var referenceConverter = ServiceLocator.Current.GetInstance<ReferenceConverter>();
                var contentLink = referenceConverter.GetContentLink(code);

                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return Ok(new
                    {
                        Description = "Step 8: Content not found for the given code.",
                        Code = code,
                        Found = false
                    });
                }

                // Current behavior: load in single language
                var currentLang = _languageAccessor.Language;
                IContent singleLangContent = null;
                _contentLoader.TryGet(contentLink, currentLang, out singleLangContent);

                // Fix: load ALL language branches
                var allBranches = new List<object>();
                var enabledLanguages = _languageBranchRepository.ListEnabled();
                foreach (var lang in enabledLanguages)
                {
                    IContent langContent = null;
                    var culture = new CultureInfo(lang.LanguageID);
                    var loaded = _contentLoader.TryGet(contentLink, culture, out langContent);

                    if (loaded && langContent != null)
                    {
                        var localizable = langContent as ILocalizable;
                        allBranches.Add(new
                        {
                            Language = lang.LanguageID,
                            Name = langContent.Name,
                            ContentLink = langContent.ContentLink.ToString(),
                            MasterLanguage = localizable?.MasterLanguage?.Name,
                            ExistingLanguages = localizable?.ExistingLanguages?.Select(el => el.Name).ToList()
                        });
                    }
                }

                return Ok(new
                {
                    Description = "Step 8: Compares loading content in the current language vs loading ALL language branches. The fix for the search issue is to load all branches.",
                    Code = code,
                    ContentLink = contentLink.ToString(),
                    CurrentBehavior = new
                    {
                        Label = "ProductSearchProviderBase loads content with _contentLoader.GetItems(keys, culture)",
                        Language = currentLang?.Name,
                        Content = singleLangContent != null ? new { singleLangContent.Name, singleLangContent.ContentLink } : null,
                        Problem = "Only one language version is returned"
                    },
                    FixedBehavior = new
                    {
                        Label = "Load content in each available language branch",
                        BranchCount = allBranches.Count,
                        Branches = allBranches
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
