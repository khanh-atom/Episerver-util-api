using EPiServer.Find;
using EPiServer.Find.Api.Querying.Queries;
using EPiServer.Find.Cms;
using EPiServer.Find.Cms.SearchProviders;
using EPiServer.Find.Commerce;
using EPiServer.Find.Framework.UI.Localization;
using EPiServer.Find.UnifiedSearch;
using EPiServer.Globalization;
using EPiServer.Shell;
using EPiServer.Shell.Search;
using Foundation.Infrastructure.Find;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Linq.Expressions;
using System.Text;
using Validator = EPiServer.Framework.Validator;
using System.Globalization;


namespace Dada.Commerce.SearchProvider
{

    [SearchProvider]
    public class ImprovedEnterpriseCatalogSearchProvider : EnterpriseContentSearchProviderBase<CatalogContentBase, ContentType>
    {

        private readonly UIDescriptorRegistry _uiDescriptorRegistry;        
        private readonly IContentLoader _contentLoader;
        private readonly IClient _searchClient;
        private readonly ILogger<ImprovedEnterpriseCatalogSearchProvider> _logger;

        private const string AllowedTypes = "allowedTypes";
        private const string RestrictedTypes = "restrictedTypes";

        private static readonly string SearchArea = "Commerce/Catalog";

        public ImprovedEnterpriseCatalogSearchProvider(
            LocalizationService localizationService,
            ISiteDefinitionResolver siteDefinitionResolver,
            IContentTypeRepository contentTypeRepository,
            UIDescriptorRegistry uiDescriptorRegistry,
            EditUrlResolver editUrlResolver,
            ServiceAccessor<SiteDefinition> currentSiteDefinition,
            IContentLanguageAccessor languageResolver,
            IUrlResolver urlResolver,
            ITemplateResolver templateResolver,
            IContentRepository contentRepository,            
            IContentLoader contentLoader,
            IClient searchClient,
             ILogger<ImprovedEnterpriseCatalogSearchProvider> logger)
            : base(
                localizationService,
                siteDefinitionResolver,
                contentTypeRepository,
                uiDescriptorRegistry,
                editUrlResolver,
                currentSiteDefinition,
                languageResolver,
                urlResolver,
                templateResolver,
                contentRepository)
        {
            // set the EditPath function to return the Commerce edit Url instead of CMS one
            EditPath = GetEditPath;
            _uiDescriptorRegistry = uiDescriptorRegistry;            
            _contentLoader = contentLoader;
            _searchClient = searchClient;            
            _logger = logger;
        }

        private string GetEditPath(CatalogContentBase content, ContentReference contentLink, string languageName)
        {
            var catalogPath = Paths.ToResource("Commerce", "Catalog");
            if (catalogPath == "/")
            {
                catalogPath = Paths.ToResource("CMS", null);
            }

            if (!string.IsNullOrWhiteSpace(languageName))
            {
                return string.Format("{0}?language={1}#context={2}", catalogPath, languageName, content.GetUri());
            }

            return string.Format("{0}#context={1}", catalogPath, content.GetUri());
        }

        public override string Area => SearchArea;

        public override string Category => "[Improved] " + Text.Translate("/commerce/searchprovider/product/name");

        protected override string IconCssClass => FindContentSearchProviderConstants.PageIconCssClass;

        public override IEnumerable<SearchResult> Search(Query query)
        {                      
            Validator.ThrowIfNull("SearchProviderFactory.Instance.AccessFilter", FilterFactory.Instance.ContentAccessFilter);
            Validator.ThrowIfNull("SearchProviderFactory.Instance.CultureFilter", FilterFactory.Instance.CultureFilter);
            Validator.ThrowIfNull("SearchProviderFactory.Instance.RootsFilter", FilterFactory.Instance.RootsFilter);
         
            ITypeSearch<IContentData> searchQuery = GetFieldQuery(query.SearchQuery, query.MaxResults)
                .Filter(x => x.MatchTypeHierarchy(typeof(CatalogContentBase)));

            var allowedTypes = GetContentTypesFromQuery(AllowedTypes, query);
            var restrictedTypes = GetContentTypesFromQuery(RestrictedTypes, query);

            FilterContext filterContext = FilterContext.Create<CatalogContentBase, ContentType>(query);
            searchQuery = FilterFactory.Instance.AllowedTypesFilter(searchQuery, filterContext, allowedTypes);
            searchQuery = FilterFactory.Instance.RestrictedTypesFilter(searchQuery, filterContext, restrictedTypes);
            searchQuery = FilterFactory.Instance.ContentAccessFilter(searchQuery, filterContext);
            searchQuery = FilterFactory.Instance.CultureFilter(searchQuery, filterContext);
            searchQuery = FilterFactory.Instance.RootsFilter(searchQuery, filterContext);

            var contentLinksWithLanguage = Enumerable.Empty<ContentInLanguageReference>();

            try
            {
                contentLinksWithLanguage = searchQuery
                    .Select(x =>
                        new ContentInLanguageReference(
                            new ContentReference(((IContent)x).ContentLink.ID,
                                                 ((IContent)x).ContentLink.ProviderName),
                            ((ILocalizable)x).Language.Name))
                    .StaticallyCacheFor(TimeSpan.FromMinutes(1), UnifiedWeightsCache.ChangeToken)
                    .GetResultAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during CMS page search query execution: {Message}", ex.Message);
                return Enumerable.Empty<SearchResult>();
            }

            CatalogContentBase content = null;

            return contentLinksWithLanguage
                .Where(
                    searchResult =>
                    _contentLoader.TryGet<CatalogContentBase>(searchResult.ContentLink,
                                                       !String.IsNullOrEmpty(searchResult.Language)
                                                           ? new CultureInfo(searchResult.Language)
                                                           : CultureInfo.CurrentCulture, out content))
                .Select(item =>
                {
                    var result = CreateSearchResult(content);
                    
                    // Get catalog name for the current content
                    var catalogName = GetCatalogName(content);

                    if (!string.IsNullOrEmpty(catalogName))
                    {
                        result.Metadata.Add("SortKey", catalogName);
                        result.Title = $"{catalogName} \\ {result.Title}"; // Prefix the page title with the catalog name
                  
                    }

                    return result;

                })
                .OrderBy(x => x.Metadata.TryGetValue("SortKey", out var sortKey) ? sortKey : string.Empty);

        }




        public string GetCatalogName(CatalogContentBase content)
        {
            if (content is CatalogContent) { 
                return string.Empty;
            }

            var referenceConverter = ServiceLocator.Current.GetInstance<ReferenceConverter>();
            
            var catalogReference = referenceConverter.GetContentLink(content.CatalogId, CatalogContentType.Catalog,0);

            if (_contentLoader.TryGet(catalogReference, out CatalogContent catalog))
            {
                return catalog.Name;
            }

            return string.Empty;
        }

        protected override ITypeSearch<IContentData> GetFieldQuery(string searchQuery, int maxResults)
        {
            var findQuery = _searchClient.Search<CatalogContentBase>();

            searchQuery = searchQuery.Trim();

            if (string.IsNullOrEmpty(searchQuery))
            {
                return findQuery.Take(maxResults);
            }

            return findQuery.For(searchQuery)
                .InField(x => x.SearchTitle(), 10)   // high boost for title matches
                .InField(x => x.SearchText(), 1)     // lower boost for text matches
                .WildcardMatch(
                    (x => x.SearchTitle(), 2.0),     // higher boost for wildcard matches in title
                    (x => x.SearchText(), 0.5))      // lower boost for wildcard matches in text
                .BoostMatching(
                    x => x.SearchTitle().PrefixCaseInsensitive(searchQuery.ToLowerInvariant()),
                    10)                              // high boost for title prefix matches - could be done per term as well
                .BoostMatching(
                    x => ((EntryContentBase)x).EscapedQueryableCode().PrefixCaseInsensitive(searchQuery.EscapeForQuery()),
                    20)                              // very high boost for code matches
                .BoostMatching(
                    x => x.MatchType(typeof(ProductContent)),
                    2)                               // boost ProductContent content types
                .BoostMatching(
                    x => x.MatchType(typeof(VariationContent)),
                    1)                               // boost VariationContent other content types
                .Take(maxResults <= 10 ? 20 : maxResults); // return more results when maxResults is low/default
        }
        private IEnumerable<Type> GetContentTypesFromQuery(string parameter, Query query)
        {
            if (query.Parameters.ContainsKey(parameter))
            {
                var array = query.Parameters[parameter] as JArray;
                if (array != null)
                {
                    return array.Values<string>().SelectMany(GetContentTypes);
                }
            }
            return Enumerable.Empty<Type>();
        }

        private new IEnumerable<Type> GetContentTypes(string allowedType)
        {
            var uiDescriptor = _uiDescriptorRegistry.UIDescriptors.FirstOrDefault(d => d.TypeIdentifier.Equals(allowedType, StringComparison.OrdinalIgnoreCase));
            if (uiDescriptor == null)
                return Enumerable.Empty<Type>();

            return _contentTypeRepository
                .List()
                .Where(c => uiDescriptor.ForType.IsAssignableFrom(c.ModelType))
                .Select(c => c.ModelType);
        }

        private Language ResolveSupportedLanguageBasedOnPreferredCulture()
        {
            Language language = null;
            var preferredCulture = ContentLanguage.PreferredCulture;
            if (preferredCulture != null)
            {
                language = _searchClient.Settings.Languages.GetSupportedLanguage(preferredCulture);
            }
            language = language ?? Language.None;
            return language;
        }
    }
    }

    public static class SearchExtensions
    {
        public static IQueriedSearch<TSource, QueryStringQuery> WildcardMatch<TSource>(
            this IQueriedSearch<TSource, QueryStringQuery> search,
            params (Expression<Func<TSource, string>> Field, double Boost)[] fieldBoosts)
        {
            return new Search<TSource, QueryStringQuery>(search, context =>
            {
                var queryStringQuery = context.RequestBody.Query as QueryStringQuery;
                if (queryStringQuery == null)
                {
                    // not a QueryStringQuery, skip
                    return;
                }

                string searchQuery = queryStringQuery.RawQuery;

                if (string.IsNullOrEmpty(searchQuery))
                    return;

                var words = searchQuery
                    .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(word =>
                        word.Length >= 2 && word.Length <= 16 &&
                        !string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(word, "OR", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .Take(5)
                    .ToList();

                var wildcardQueries = new List<WildcardQuery>();

                foreach (var (field, boost) in fieldBoosts)
                {
                    string fieldName = search.Client.Conventions
                        .FieldNameConvention
                        .GetFieldNameForAnalyzed(field);

                    foreach (var word in words)
                    {
                        wildcardQueries.Add(new WildcardQuery(fieldName, word + "*")
                        {
                            Boost = boost
                        });
                    }
                }

                var boolQuery = new BoolQuery();

                if (context.RequestBody.Query != null)
                {
                    boolQuery.Should.Add(context.RequestBody.Query);
                }

                foreach (var wildcardQuery in wildcardQueries)
                {
                    boolQuery.Should.Add(wildcardQuery);
                }

                boolQuery.MinimumNumberShouldMatch = 1;
                context.RequestBody.Query = boolQuery;
            });
        }

        public static string EscapeForQuery(this string input)
        {
            if (input == null)
            {
                return null;
            }

            var buffer = new StringBuilder();

            foreach (var c in input)
            {
                // 0-9: [0x30, 0x39], A-Z: [0x41, 0x5A], a-z: [0x61, 0x7A]
                if (c > 'z' || c < '0' || (c > '9' && c < 'A') || (c > 'Z' && c < 'a'))
                {
                    // Non-ascii or non-alphanumeric. Write unicode position in hex instead.
                    buffer.AppendFormat("{0:X}", (int)c);
                }
                else
                {
                    buffer.Append(c);
                }
            }

            return buffer.ToString();
        }
    }

