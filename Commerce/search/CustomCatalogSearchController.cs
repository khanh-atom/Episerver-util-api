using EPiServer.Shell.Search;
using Mediachase.Commerce.Core;
using Mediachase.Search;
using Mediachase.Search.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.Search
{
    /// <summary>
    /// API controller to replicate and debug NullReferenceException in Commerce Catalog search.
    /// This walks through the call chain: SearchProvider → SearchManager → SearchService → Provider.
    /// Route: util-api/custom-catalog-search
    /// </summary>
    [ApiController]
    [Route("util-api/custom-catalog-search")]
    public class CustomCatalogSearchController : ControllerBase
    {
        private readonly IEnumerable<ISearchProvider> _searchProviders;
        private readonly ServiceAccessor<SearchManager> _searchManagerAccessor;
        private readonly IOptions<SearchOptions> _searchOptions;
        private readonly ServiceAccessor<SiteContext> _siteContextAccessor;
        private readonly IContentLanguageAccessor _languageAccessor;
        private readonly IWebHostEnvironment _hostEnvironment;

        public CustomCatalogSearchController(
            IEnumerable<ISearchProvider> searchProviders,
            ServiceAccessor<SearchManager> searchManagerAccessor,
            IOptions<SearchOptions> searchOptions,
            ServiceAccessor<SiteContext> siteContextAccessor,
            IContentLanguageAccessor languageAccessor,
            IWebHostEnvironment hostEnvironment)
        {
            _searchProviders = searchProviders;
            _searchManagerAccessor = searchManagerAccessor;
            _searchOptions = searchOptions;
            _siteContextAccessor = siteContextAccessor;
            _languageAccessor = languageAccessor;
            _hostEnvironment = hostEnvironment;
        }

        /// <summary>
        /// Step 1: List all registered search providers and their status.
        /// Shows which providers are available in the Commerce/Catalog area.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/list-providers
        /// </summary>
        [HttpGet("list-providers")]
        public IActionResult ListProviders()
        {
            try
            {
                var allProviders = _searchProviders.ToList();
                var catalogProviders = allProviders
                    .Where(p => p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var result = new
                {
                    Description = "Step 1: Lists all registered search providers. The catalog search UI calls CompositeSearch with multiple providers (e.g., EnterpriseCatalogSearchProvider + ProtectedProductSearchProvider).",
                    TotalProviders = allProviders.Count,
                    CatalogProviders = catalogProviders.Select(p => new
                    {
                        Key = p.GetType().FullName?.Replace('.', '_'),
                        Type = p.GetType().FullName,
                        Area = p.Area,
                        Category = p.Category
                    }).ToList(),
                    AllProviders = allProviders.Select(p => new
                    {
                        Key = p.GetType().FullName?.Replace('.', '_'),
                        Type = p.GetType().FullName,
                        Area = p.Area,
                        Category = p.Category
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Check the search index configuration and health.
        /// Verifies if SearchManager, SearchOptions, and Indexers are properly configured.
        /// This is the typical root cause when NullReferenceException occurs in deployed environments.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/check-search-config
        /// </summary>
        [HttpGet("check-search-config")]
        public IActionResult CheckSearchConfig()
        {
            try
            {
                var options = _searchOptions.Value;

                SearchManager searchManager = null;
                string searchManagerError = null;
                try
                {
                    searchManager = _searchManagerAccessor();
                }
                catch (Exception ex)
                {
                    searchManagerError = $"{ex.GetType().Name}: {ex.Message}";
                }

                IndexBuilder[] indexBuilders = null;
                string indexBuilderError = null;
                try
                {
                    if (searchManager != null)
                    {
                        indexBuilders = searchManager.GetIndexBuilders();
                    }
                }
                catch (Exception ex)
                {
                    indexBuilderError = $"{ex.GetType().Name}: {ex.Message}";
                }

                var result = new
                {
                    Description = "Step 2: Checks the search configuration. NullReferenceException typically happens when the search provider/index is not properly initialized.",
                    SearchOptions = new
                    {
                        DefaultSearchProvider = options.DefaultSearchProvider,
                        MaxHitsForSearchResults = options.MaxHitsForSearchResults,
                        IndexerBasePath = options.IndexerBasePath,
                        SearchProviders = options.SearchProviders?.Select(sp => new
                        {
                            sp.Name,
                            sp.Type,
                            Parameters = sp.Parameters?.ToDictionary(k => k.Key, v => v.Value)
                        }).ToList(),
                        Indexers = options.Indexers?.Select(idx => new
                        {
                            idx.Name,
                            idx.Type
                        }).ToList()
                    },
                    SearchManager = new
                    {
                        IsResolved = searchManager != null,
                        Error = searchManagerError
                    },
                    IndexBuilders = new
                    {
                        Count = indexBuilders?.Length ?? 0,
                        Items = indexBuilders?.Select(ib => new
                        {
                            ib.IndexerName,
                            ib.ApplicationName,
                            ib.DirectoryPath,
                            DirectoryExists = !string.IsNullOrEmpty(ib.DirectoryPath) && System.IO.Directory.Exists(ib.DirectoryPath)
                        }).ToList(),
                        Error = indexBuilderError
                    },
                    Environment = new
                    {
                        EnvironmentName = _hostEnvironment.EnvironmentName,
                        IsDevelopment = _hostEnvironment.IsDevelopment()
                    }
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Execute search directly via SearchManager.Search() with CatalogEntrySearchCriteria.
        /// This replicates the exact call path: ProductSearchProviderBase.ExecuteProviderSearch() → SearchManager.Search().
        /// If the search index is misconfigured, this will throw NullReferenceException.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/search-via-manager?keyword=P-407
        /// </summary>
        [HttpGet("search-via-manager")]
        public IActionResult SearchViaManager([FromQuery] string keyword = "P-407")
        {
            try
            {
                var searchManager = _searchManagerAccessor();
                if (searchManager == null)
                {
                    return Ok(new
                    {
                        Description = "Step 3: SearchManager is null. This would cause NullReferenceException.",
                        SearchManagerResolved = false
                    });
                }

                var criteria = CreateCriteria();

                // This is what ProductSearchProviderBase does - appends wildcard
                criteria.Add(
                    BaseCatalogIndexBuilder.FieldConstants.Code,
                    new SimpleValue
                    {
                        key = BaseCatalogIndexBuilder.FieldConstants.Code,
                        value = keyword + "*"
                    });

                ISearchResults searchResult = null;
                string searchError = null;
                try
                {
                    searchResult = searchManager.Search(criteria);
                }
                catch (Exception ex)
                {
                    searchError = $"{ex.GetType().Name}: {ex.Message}";
                }

                var result = new
                {
                    Description = "Step 3: Executes SearchManager.Search() directly. This is the exact call that throws NullReferenceException when the search index is misconfigured.",
                    Keyword = keyword,
                    CriteriaKeywordWithWildcard = keyword + "*",
                    SearchSucceeded = searchResult != null,
                    Error = searchError,
                    TotalCount = searchResult?.TotalCount ?? 0,
                    Documents = searchResult?.Documents?.Take(5).Select(doc =>
                    {
                        var fields = new List<object>();
                        for (int i = 0; i < Math.Min(doc.FieldCount, 10); i++)
                        {
                            var field = doc[i];
                            fields.Add(new { Name = field?.Name, Value = field?.Value });
                        }
                        return new { Fields = fields };
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Execute search via the ProtectedProductSearchProvider (the exact provider in the stack trace).
        /// This replicates the full call chain and shows the null-return bug when NullReferenceException is caught.
        /// The bug: ProductSearchProviderBase.SearchEntries() initializes result = null, catches NullReferenceException, returns null.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/search-via-provider?keyword=P-407
        /// </summary>
        [HttpGet("search-via-provider")]
        public IActionResult SearchViaProvider([FromQuery] string keyword = "P-407")
        {
            try
            {
                // Find the ProtectedProductSearchProvider from registered providers
                var provider = _searchProviders.FirstOrDefault(p =>
                    p.GetType().Name.Contains("ProtectedProductSearchProvider"));

                if (provider == null)
                {
                    // Fallback: try any catalog provider
                    provider = _searchProviders.FirstOrDefault(p =>
                        p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase));
                }

                if (provider == null)
                {
                    return Ok(new
                    {
                        Description = "Step 4: No Commerce/Catalog search provider found.",
                        AvailableProviders = _searchProviders.Select(p => new
                        {
                            Type = p.GetType().FullName,
                            Area = p.Area
                        }).ToList()
                    });
                }

                var query = new Query(keyword, 10);

                IEnumerable<SearchResult> searchResults = null;
                string searchError = null;
                bool isResultNull = false;

                try
                {
                    searchResults = provider.Search(query);
                    isResultNull = searchResults == null;
                }
                catch (Exception ex)
                {
                    searchError = $"{ex.GetType().Name}: {ex.Message}";
                }

                // This is the bug: if the result is null, calling .ToList() will throw ArgumentNullException
                string toListError = null;
                List<SearchResult> materialized = null;
                try
                {
                    if (searchResults != null)
                    {
                        materialized = searchResults.ToList();
                    }
                    else
                    {
                        // Simulate what SearchResultStore.CompositeSearch does:
                        // provider.Search(query).ToList() - if Search() returns null, this throws
                        toListError = "Search returned null. In production, calling .ToList() on null causes: ArgumentNullException: Value cannot be null. (Parameter 'source')";
                    }
                }
                catch (Exception ex)
                {
                    toListError = $"{ex.GetType().Name}: {ex.Message}";
                }

                var result = new
                {
                    Description = "Step 4: Executes search via the exact provider from the stack trace. Shows the null-return bug when NullReferenceException is caught internally.",
                    ProviderType = provider.GetType().FullName,
                    Keyword = keyword,
                    SearchError = searchError,
                    IsResultNull = isResultNull,
                    ToListError = toListError,
                    Bug = isResultNull
                        ? "BUG CONFIRMED: ProductSearchProviderBase.SearchEntries() initializes 'result = null' (line 181). When NullReferenceException is caught (line 223), the method returns null. SearchResultStore.CompositeSearch() calls .ToList() on null → ArgumentNullException."
                        : null,
                    ResultCount = materialized?.Count ?? 0,
                    Results = materialized?.Take(5).Select(sr => new
                    {
                        sr.Title,
                        sr.PreviewText,
                        Metadata = sr.Metadata
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Simulate the full CompositeSearch flow as SearchResultStore does.
        /// Calls all catalog search providers, merges results.
        /// If any returns null, shows the ArgumentNullException that occurs in production.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/composite-search?keyword=P-407
        /// </summary>
        [HttpGet("composite-search")]
        public IActionResult CompositeSearch([FromQuery] string keyword = "P-407")
        {
            try
            {
                var catalogProviders = _searchProviders
                    .Where(p => p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var query = new Query(keyword, 10);
                var perProviderResults = new List<object>();
                var allResults = new List<SearchResult>();
                var hasNullReturn = false;

                foreach (var provider in catalogProviders)
                {
                    IEnumerable<SearchResult> providerResults = null;
                    string error = null;
                    bool isNull = false;

                    try
                    {
                        providerResults = provider.Search(query);
                        isNull = providerResults == null;
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    if (isNull)
                    {
                        hasNullReturn = true;
                    }

                    // Simulate .ToList() call like SearchResultStore does
                    string toListError = null;
                    int count = 0;
                    try
                    {
                        if (providerResults != null)
                        {
                            var list = providerResults.ToList();
                            count = list.Count;
                            allResults.AddRange(list.Take(25));
                        }
                        else if (error == null)
                        {
                            toListError = "ArgumentNullException would be thrown here - provider.Search() returned null";
                        }
                    }
                    catch (Exception ex)
                    {
                        toListError = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    perProviderResults.Add(new
                    {
                        ProviderType = provider.GetType().FullName,
                        SearchError = error,
                        IsResultNull = isNull,
                        ToListError = toListError,
                        ResultCount = count
                    });
                }

                var result = new
                {
                    Description = "Step 5: Simulates the full CompositeSearch flow exactly as SearchResultStore does. Calls all catalog providers and merges results.",
                    Keyword = keyword,
                    ProvidersSearched = perProviderResults.Count,
                    HasNullReturn = hasNullReturn,
                    BugTriggered = hasNullReturn
                        ? "BUG: One or more providers returned null. In production SearchResultStore.CompositeSearch(), calling .ToList() on null throws ArgumentNullException."
                        : "No bug triggered - all providers returned non-null results.",
                    TotalMergedResults = allResults.Count,
                    PerProviderResults = perProviderResults,
                    MergedResults = allResults.Take(10).Select(sr => new
                    {
                        sr.Title,
                        sr.PreviewText,
                        Metadata = sr.Metadata
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Test search with phrase fallback (replicates the full ProductSearchProviderBase.SearchEntries logic).
        /// First tries code prefix match, then phrase match, then fuzzy match.
        /// Sample: https://localhost:5009/util-api/custom-catalog-search/search-with-fallback?keyword=ANDREA
        /// </summary>
        [HttpGet("search-with-fallback")]
        public IActionResult SearchWithFallback([FromQuery] string keyword = "ANDREA")
        {
            try
            {
                var searchManager = _searchManagerAccessor();

                var steps = new List<object>();

                // Step A: Code prefix match (keyword + "*")
                var criteriaA = CreateCriteria();
                criteriaA.Add(
                    BaseCatalogIndexBuilder.FieldConstants.Code,
                    new SimpleValue
                    {
                        key = BaseCatalogIndexBuilder.FieldConstants.Code,
                        value = keyword + "*"
                    });

                ISearchResults resultA = null;
                string errorA = null;
                try
                {
                    resultA = searchManager.Search(criteriaA);
                }
                catch (Exception ex)
                {
                    errorA = $"{ex.GetType().Name}: {ex.Message}";
                }

                steps.Add(new
                {
                    Step = "A - Code prefix match",
                    Query = keyword + "*",
                    Error = errorA,
                    TotalCount = resultA?.TotalCount ?? 0
                });

                // Step B: Phrase match (if code match returned 0)
                ISearchResults resultB = null;
                string errorB = null;
                if ((resultA?.TotalCount ?? 0) == 0)
                {
                    var criteriaB = CreateCriteria();
                    criteriaB.SearchPhrase = System.Text.RegularExpressions.Regex.Replace(keyword.Trim(), @"\s+", " ");
                    try
                    {
                        resultB = searchManager.Search(criteriaB);
                    }
                    catch (Exception ex)
                    {
                        errorB = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    steps.Add(new
                    {
                        Step = "B - Phrase match",
                        Query = keyword,
                        Error = errorB,
                        TotalCount = resultB?.TotalCount ?? 0
                    });
                }

                // Step C: Fuzzy match (if phrase match also returned 0)
                ISearchResults resultC = null;
                string errorC = null;
                if ((resultB?.TotalCount ?? 0) == 0 && (resultA?.TotalCount ?? 0) == 0)
                {
                    var criteriaC = CreateCriteria();
                    criteriaC.SearchPhrase = keyword;
                    criteriaC.IsFuzzySearch = true;
                    criteriaC.FuzzyMinSimilarity = 0.7f;
                    try
                    {
                        resultC = searchManager.Search(criteriaC);
                    }
                    catch (Exception ex)
                    {
                        errorC = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    steps.Add(new
                    {
                        Step = "C - Fuzzy match",
                        Query = keyword,
                        FuzzyMinSimilarity = 0.7f,
                        Error = errorC,
                        TotalCount = resultC?.TotalCount ?? 0
                    });
                }

                // Pick the first non-zero result
                var finalResult = resultA?.TotalCount > 0 ? resultA :
                                  resultB?.TotalCount > 0 ? resultB :
                                  resultC?.TotalCount > 0 ? resultC : null;

                var result = new
                {
                    Description = "Step 6: Replicates the full ProductSearchProviderBase.SearchEntries() fallback logic: Code prefix → Phrase → Fuzzy.",
                    Keyword = keyword,
                    SearchSteps = steps,
                    FinalResultCount = finalResult?.TotalCount ?? 0,
                    FinalDocuments = finalResult?.Documents?.Take(5).Select(doc =>
                    {
                        var fields = new List<object>();
                        for (int i = 0; i < Math.Min(doc.FieldCount, 10); i++)
                        {
                            var field = doc[i];
                            fields.Add(new { Name = field?.Name, Value = field?.Value });
                        }
                        return new { Fields = fields };
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private CatalogEntrySearchCriteria CreateCriteria()
        {
            return new CatalogEntrySearchCriteria
            {
                Currency = _siteContextAccessor().Currency.CurrencyCode,
                Sort = CatalogEntrySearchCriteria.DefaultSortOrder,
                IgnoreFilterOnLanguage = true,
                StartingRecord = 0,
                RecordsToRetrieve = 10,
                Locale = _languageAccessor.Language?.Name ?? "en"
            };
        }
    }
}
