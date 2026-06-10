using EPiServer.Find;
using EPiServer.Find.Cms;
using Dada.Commerce.SearchProvider;
using EPiServer.Find.Commerce;
using EPiServer.Find.Commerce.Extensions;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Core;
using EPiServer.Shell.Search;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.Search
{
    /// <summary>
    /// API controller to replicate and debug wildcard character escaping issue
    /// in EnterpriseCatalogSearchProvider. The .For() method internally escapes
    /// the wildcard character (*), so "021054*" becomes "021054\\*" in ElasticSearch,
    /// effectively breaking wildcard search.
    /// Route: util-api/custom-wildcard-search
    /// </summary>
    [ApiController]
    [Route("util-api/custom-wildcard-search")]
    public class CustomWildcardSearchController : ControllerBase
    {
        private readonly IClient _client;
        private readonly IEnumerable<ISearchProvider> _searchProviders;

        public CustomWildcardSearchController(
            IClient client,
            IEnumerable<ISearchProvider> searchProviders)
        {
            _client = client;
            _searchProviders = searchProviders;
        }

        /// <summary>
        /// Step 1: Show how EnterpriseCatalogSearchProvider.GetFieldQuery builds the wildcard query string.
        /// Demonstrates the wildcard string builder logic that appends "*" to each word.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/build-wildcard-string?searchQuery=SKU123
        /// </summary>
        [HttpGet("build-wildcard-string")]
        public IActionResult BuildWildcardString([FromQuery] string searchQuery = "SKU123")
        {
            try
            {
                // Replicate the exact logic from EnterpriseCatalogSearchProvider.GetFieldQuery
                var wildcardQueryBuilder = new StringBuilder();
                if (string.IsNullOrEmpty(searchQuery))
                {
                    searchQuery = "*";
                    wildcardQueryBuilder.Append('*');
                }
                else
                {
                    var words = searchQuery.Trim().Split(' ');
                    foreach (var word in words)
                    {
                        wildcardQueryBuilder.Append(word);
                        if (string.Equals("AND", word.Trim(), StringComparison.Ordinal) ||
                            string.Equals("OR", word.Trim(), StringComparison.Ordinal))
                        {
                            wildcardQueryBuilder.Append(' ');
                        }
                        else
                        {
                            wildcardQueryBuilder.Append("* ");
                        }
                    }
                }

                var builtString = wildcardQueryBuilder.ToString();

                // Also show the escaped code version
                var escapedQuery = searchQuery.EscapeForQuery();

                return Ok(new
                {
                    Description = "Step 1: Shows how EnterpriseCatalogSearchProvider.GetFieldQuery builds the wildcard query string. Each word gets '*' appended. This string is then passed to .For() which internally escapes the '*' character.",
                    OriginalSearchQuery = searchQuery,
                    WildcardQueryBuilderResult = builtString,
                    EscapedCodeVersion = escapedQuery,
                    Problem = "The built string (e.g. 'SKU123* ') is passed to .For() which internally escapes '*' to '\\\\*', so ElasticSearch receives a literal asterisk instead of a wildcard operator.",
                    SourceCodeReference = new
                    {
                        File = "EnterpriseCatalogSearchProvider.cs",
                        Method = "GetFieldQuery",
                        Line138 = ".For(wildcardQueryBuilder.ToString())",
                        Line139 = ".InField(x => x.SearchText())",
                        Line140 = ".BoostMatching(x => ((EntryContentBase)x).EscapedQueryableCode().Match(searchQuery.EscapeForQuery()), 2)"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Execute the CURRENT (buggy) search using .For() with wildcard string.
        /// The .For() method escapes the wildcard character, so the search effectively
        /// looks for a literal asterisk character instead of performing a wildcard search.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/search-current-buggy?searchQuery=SKU123
        /// </summary>
        [HttpGet("search-current-buggy")]
        public IActionResult SearchCurrentBuggy([FromQuery] string searchQuery = "SKU123", [FromQuery] int maxResults = 10)
        {
            try
            {
                // Build wildcard string exactly as EnterpriseCatalogSearchProvider does
                var wildcardQueryBuilder = new StringBuilder();
                if (string.IsNullOrEmpty(searchQuery))
                {
                    searchQuery = "*";
                    wildcardQueryBuilder.Append('*');
                }
                else
                {
                    var words = searchQuery.Trim().Split(' ');
                    foreach (var word in words)
                    {
                        wildcardQueryBuilder.Append(word);
                        if (string.Equals("AND", word.Trim(), StringComparison.Ordinal) ||
                            string.Equals("OR", word.Trim(), StringComparison.Ordinal))
                        {
                            wildcardQueryBuilder.Append(' ');
                        }
                        else
                        {
                            wildcardQueryBuilder.Append("* ");
                        }
                    }
                }

                var builtQuery = wildcardQueryBuilder.ToString();

                // This is the BUGGY path: .For() escapes the '*'
                var search = _client.Search<CatalogContentBase>()
                    .For(builtQuery)
                    .InField(x => x.SearchText())
                    .BoostMatching(x => ((EntryContentBase)x).EscapedQueryableCode().Match(searchQuery.EscapeForQuery()), 2)
                    .Take(maxResults);


                var results = search.GetContentResult();
                var items = results.Take(10).Select(c => new
                {
                    Name = (c as IContent)?.Name,
                    Code = (c as EntryContentBase)?.Code,
                    ContentType = c.GetType().Name,
                    ContentLink = (c as IContent)?.ContentLink?.ToString()
                }).ToList();

                return Ok(new
                {
                    Description = "Step 2: Executes the CURRENT (buggy) search path. .For() internally escapes '*' so the query searches for a literal asterisk instead of doing wildcard matching.",
                    SearchQuery = searchQuery,
                    WildcardBuiltQuery = builtQuery,
                    TotalResults = results.TotalMatching,
                    ResultCount = items.Count,
                    Results = items,

                    Bug = "The .For() method escapes special characters including '*'. The ElasticSearch query_string receives '\\\\*' which matches a literal '*' character, not a wildcard."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Execute the FIXED search using bool query with separate WildcardQuery.
        /// Instead of relying on .For() (which escapes '*'), this approach builds a
        /// proper bool query with explicit WildcardQuery objects that are not escaped.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/search-fixed-wildcard?searchQuery=SKU123
        /// </summary>
        [HttpGet("search-fixed-wildcard")]
        public IActionResult SearchFixedWildcard([FromQuery] string searchQuery = "SKU123", [FromQuery] int maxResults = 10)
        {
            try
            {
                searchQuery = searchQuery.Trim();

                if (string.IsNullOrEmpty(searchQuery))
                {
                    return Ok(new { Description = "Empty search query", Results = new List<object>() });
                }

                // FIXED approach: use .For() for the query_string part (text relevance)
                // then add explicit WildcardQuery via .WildcardMatch extension
                var search = _client.Search<CatalogContentBase>()
                    .For(searchQuery)
                    .InField(x => x.SearchTitle(), 10)
                    .InField(x => x.SearchText(), 1)
                    .WildcardMatch(
                        (x => x.SearchTitle(), 2.0),
                        (x => x.SearchText(), 0.5))
                    .BoostMatching(
                        x => x.SearchTitle().PrefixCaseInsensitive(searchQuery.ToLowerInvariant()), 10)
                    .BoostMatching(
                        x => ((EntryContentBase)x).EscapedQueryableCode().PrefixCaseInsensitive(searchQuery.EscapeForQuery()), 20)
                    .Take(maxResults <= 10 ? 20 : maxResults);



                var results = search.GetContentResult();
                var items = results.Take(10).Select(c => new
                {
                    Name = (c as IContent)?.Name,
                    Code = (c as EntryContentBase)?.Code,
                    ContentType = c.GetType().Name,
                    ContentLink = (c as IContent)?.ContentLink?.ToString()
                }).ToList();

                return Ok(new
                {
                    Description = "Step 3: Executes the FIXED search using bool query with explicit WildcardQuery objects. The WildcardQuery is NOT escaped by .For(), so 'SKU123*' actually performs a wildcard match.",
                    SearchQuery = searchQuery,
                    TotalResults = results.TotalMatching,
                    ResultCount = items.Count,
                    Results = items,

                    Fix = "Use a bool query with separate WildcardQuery objects (via .WildcardMatch extension) instead of appending '*' to the .For() query string. Also use PrefixCaseInsensitive for code boosting."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Compare BUGGY vs FIXED search results side by side.
        /// Shows the difference in results when using .For() with wildcard string
        /// vs using explicit WildcardQuery objects.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/compare-buggy-vs-fixed?searchQuery=SKU123
        /// </summary>
        [HttpGet("compare-buggy-vs-fixed")]
        public IActionResult CompareBuggyVsFixed([FromQuery] string searchQuery = "SKU123", [FromQuery] int maxResults = 10)
        {
            try
            {
                searchQuery = searchQuery.Trim();

                // === BUGGY: Current EnterpriseCatalogSearchProvider approach ===
                var wildcardQueryBuilder = new StringBuilder();
                var words = searchQuery.Split(' ');
                foreach (var word in words)
                {
                    wildcardQueryBuilder.Append(word);
                    if (string.Equals("AND", word.Trim(), StringComparison.Ordinal) ||
                        string.Equals("OR", word.Trim(), StringComparison.Ordinal))
                    {
                        wildcardQueryBuilder.Append(' ');
                    }
                    else
                    {
                        wildcardQueryBuilder.Append("* ");
                    }
                }

                int buggyCount = 0;
                string buggyError = null;

                List<object> buggyItems = new List<object>();
                try
                {
                    var buggySearch = _client.Search<CatalogContentBase>()
                        .For(wildcardQueryBuilder.ToString())
                        .InField(x => x.SearchText())
                        .BoostMatching(x => ((EntryContentBase)x).EscapedQueryableCode().Match(searchQuery.EscapeForQuery()), 2)
                        .Take(maxResults);



                    var buggyResults = buggySearch.GetContentResult();
                    buggyCount = buggyResults.TotalMatching;
                    buggyItems = buggyResults.Take(10).Select(c => new
                    {
                        Name = (c as IContent)?.Name,
                        Code = (c as EntryContentBase)?.Code,
                        ContentType = c.GetType().Name
                    }).Cast<object>().ToList();
                }
                catch (Exception ex)
                {
                    buggyError = $"{ex.GetType().Name}: {ex.Message}";
                }

                // === FIXED: Bool query with explicit WildcardQuery ===
                int fixedCount = 0;
                string fixedError = null;

                List<object> fixedItems = new List<object>();
                try
                {
                    var fixedSearch = _client.Search<CatalogContentBase>()
                        .For(searchQuery)
                        .InField(x => x.SearchTitle(), 10)
                        .InField(x => x.SearchText(), 1)
                        .WildcardMatch(
                            (x => x.SearchTitle(), 2.0),
                            (x => x.SearchText(), 0.5))
                        .BoostMatching(
                            x => ((EntryContentBase)x).EscapedQueryableCode().PrefixCaseInsensitive(searchQuery.EscapeForQuery()), 20)
                        .Take(maxResults <= 10 ? 20 : maxResults);



                    var fixedResults = fixedSearch.GetContentResult();
                    fixedCount = fixedResults.TotalMatching;
                    fixedItems = fixedResults.Take(10).Select(c => new
                    {
                        Name = (c as IContent)?.Name,
                        Code = (c as EntryContentBase)?.Code,
                        ContentType = c.GetType().Name
                    }).Cast<object>().ToList();
                }
                catch (Exception ex)
                {
                    fixedError = $"{ex.GetType().Name}: {ex.Message}";
                }

                return Ok(new
                {
                    Description = "Step 4: Side-by-side comparison of BUGGY (current) vs FIXED wildcard search. The buggy approach escapes '*' via .For(), the fixed approach uses explicit WildcardQuery objects.",
                    SearchQuery = searchQuery,
                    BuggyApproach = new
                    {
                        Label = "Current EnterpriseCatalogSearchProvider (BUGGY)",
                        WildcardString = wildcardQueryBuilder.ToString(),
                        Issue = ".For() escapes '*' so ElasticSearch searches for literal asterisk",
                        TotalResults = buggyCount,
                        Error = buggyError,
                        Results = buggyItems
                    },
                    FixedApproach = new
                    {
                        Label = "Bool query with explicit WildcardQuery (FIXED)",
                        Fix = "WildcardQuery objects are added to a bool.should clause, bypassing .For() escaping",
                        TotalResults = fixedCount,
                        Error = fixedError,
                        Results = fixedItems
                    },
                    Conclusion = fixedCount > buggyCount
                        ? $"FIXED approach returned {fixedCount - buggyCount} more results, confirming the wildcard escaping bug."
                        : fixedCount == buggyCount
                            ? "Both returned the same count. Try a partial code like the first few characters of a product code to see the difference."
                            : "Unexpected: buggy returned more. Check the raw request bodies for details."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Demonstrate the case-sensitivity issue with the .standard analyzer field.
        /// Wildcard queries on SearchTitle$$string.standard are case-sensitive,
        /// so "UK25*" won't match "uk25..." entries. The fix is to use a lowercase field.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/test-case-sensitivity?searchQuery=SKU123
        /// </summary>
        [HttpGet("test-case-sensitivity")]
        public IActionResult TestCaseSensitivity([FromQuery] string searchQuery = "SKU123", [FromQuery] int maxResults = 10)
        {
            try
            {
                searchQuery = searchQuery.Trim();

                // Case-sensitive wildcard (original behavior)
                int caseSensitiveCount = 0;
                string caseSensitiveError = null;
                List<object> caseSensitiveItems = new List<object>();
                try
                {
                    var caseSensitiveSearch = _client.Search<CatalogContentBase>()
                        .For(searchQuery)
                        .InField(x => x.SearchTitle())
                        .InField(x => x.SearchText())
                        .Take(maxResults);

                    var csResults = caseSensitiveSearch.GetContentResult();
                    caseSensitiveCount = csResults.TotalMatching;
                    caseSensitiveItems = csResults.Take(10).Select(c => new
                    {
                        Name = (c as IContent)?.Name,
                        Code = (c as EntryContentBase)?.Code,
                        ContentType = c.GetType().Name
                    }).Cast<object>().ToList();
                }
                catch (Exception ex)
                {
                    caseSensitiveError = $"{ex.GetType().Name}: {ex.Message}";
                }

                // Case-insensitive wildcard (using lowercase)
                int caseInsensitiveCount = 0;
                string caseInsensitiveError = null;
                List<object> caseInsensitiveItems = new List<object>();
                try
                {
                    var lowerQuery = searchQuery.ToLowerInvariant();
                    var caseInsensitiveSearch = _client.Search<CatalogContentBase>()
                        .For(lowerQuery)
                        .InField(x => x.SearchTitle())
                        .InField(x => x.SearchText())
                        .BoostMatching(
                            x => x.SearchTitle().PrefixCaseInsensitive(lowerQuery), 10)
                        .Take(maxResults);

                    var ciResults = caseInsensitiveSearch.GetContentResult();
                    caseInsensitiveCount = ciResults.TotalMatching;
                    caseInsensitiveItems = ciResults.Take(10).Select(c => new
                    {
                        Name = (c as IContent)?.Name,
                        Code = (c as EntryContentBase)?.Code,
                        ContentType = c.GetType().Name
                    }).Cast<object>().ToList();
                }
                catch (Exception ex)
                {
                    caseInsensitiveError = $"{ex.GetType().Name}: {ex.Message}";
                }

                return Ok(new
                {
                    Description = "Step 5: Demonstrates case-sensitivity issue. Wildcard queries on the .standard analyzer field are case-sensitive. 'UK25*' won't match 'uk25...' entries.",
                    SearchQuery = searchQuery,
                    LowercaseQuery = searchQuery.ToLowerInvariant(),
                    OriginalCase = new
                    {
                        Label = "Search with original case",
                        TotalResults = caseSensitiveCount,
                        Error = caseSensitiveError,
                        Results = caseSensitiveItems
                    },
                    LowercaseSearch = new
                    {
                        Label = "Search with lowercase + PrefixCaseInsensitive boost",
                        TotalResults = caseInsensitiveCount,
                        Error = caseInsensitiveError,
                        Results = caseInsensitiveItems
                    },
                    Explanation = "The .standard analyzer field (SearchTitle$$string.standard) preserves case. WildcardQuery on this field is case-sensitive. Use the .lowercase field variant or convert the query to lowercase for case-insensitive matching."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Execute search via the registered EnterpriseCatalogSearchProvider directly.
        /// This calls the actual provider's Search method to show end-to-end behavior.
        /// Sample: https://localhost:5009/util-api/custom-wildcard-search/search-via-provider?searchQuery=jodh
        /// </summary>
        [HttpGet("search-via-provider")]
        public IActionResult SearchViaProvider([FromQuery] string searchQuery = "jodh")
        {
            try
            {
                var catalogProviders = _searchProviders
                    .Where(p => p.Area.StartsWith("Commerce/Catalog", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var query = new Query(searchQuery, 10);
                var perProviderResults = new List<object>();

                foreach (var provider in catalogProviders)
                {
                    IEnumerable<SearchResult> providerResults = null;
                    string error = null;
                    int count = 0;
                    List<object> items = new List<object>();

                    try
                    {
                        providerResults = provider.Search(query);
                        if (providerResults != null)
                        {
                            var list = providerResults.ToList();
                            count = list.Count;
                            items = list.Take(10).Select(sr => new
                            {
                                sr.Title,
                                sr.PreviewText,
                                Metadata = sr.Metadata
                            }).Cast<object>().ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    if(provider.GetType().FullName.Contains("EnterpriseCatalogSearchProvider"))
                    {
                        perProviderResults.Add(new
                        {
                            ProviderType = provider.GetType().FullName,
                            ProviderCategory = provider.Category,
                            SearchError = error,
                            IsResultNull = providerResults == null,
                            ResultCount = count,
                            Results = items
                        });

                    }
                }

                return Ok(new
                {
                    Description = "Step 6: Calls the actual registered catalog search providers. Shows end-to-end behavior including the wildcard escaping bug in EnterpriseCatalogSearchProvider.",
                    SearchQuery = searchQuery,
                    ProvidersFound = catalogProviders.Count,
                    PerProviderResults = perProviderResults,
                    Note = "If EnterpriseCatalogSearchProvider returns fewer results than expected for partial codes, it confirms the wildcard escaping bug."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }
    }

}

