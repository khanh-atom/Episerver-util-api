using System.Text.Json;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Cms.NetCore.ConventionsApi;
using Optimizely.ContentGraph.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to reproduce the Content Graph interface filtering issue.
    /// When migrating from Search &amp; Navigation to Content Graph in CMS 12,
    /// interface properties are visible in query results but NOT in the where clause
    /// unless registered via IncludeInterface&lt;T&gt;().
    ///
    /// Steps:
    /// 1. Config overview + all sample URLs
    /// 2. Inspect C# interfaces on a content item
    /// 3. Introspect live Graph schema for the interface type
    /// 4. Reproduce FAILING query (interface field in Content where clause)
    /// 5. Register interface via ConventionRepository
    /// 6. Query using interface type as root (CORRECT approach)
    /// 7. Query interface type with where clause filter (THE GOAL)
    /// 8. Schema check — verify type exists for code generation tools
    /// </summary>
    [ApiController]
    [Route("util-api/custom-interface-filter")]
    public class CustomInterfaceFilterController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IClient _graphClient;
        private readonly IServiceProvider _serviceProvider;
        ConventionRepository _conventionRepository;

        public CustomInterfaceFilterController(
            IContentLoader contentLoader,
            IContentTypeRepository contentTypeRepository,
            IOptions<QueryOptions> queryOptions,
            IClient graphClient,
            ConventionRepository conventionRepository,
            IServiceProvider serviceProvider)
        {
            _contentLoader = contentLoader;
            _contentTypeRepository = contentTypeRepository;
            _queryOptions = queryOptions;
            _graphClient = graphClient;
            _serviceProvider = serviceProvider;
            _conventionRepository = conventionRepository;
        }

        /// <summary>
        /// Step 1: Shows current Content Graph configuration and all sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/config
        /// </summary>
        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            try
            {
                var opts = _queryOptions.Value;
                return Ok(new
                {
                    Step = "1 - Configuration & Overview",
                    Description = "Reproduces the Content Graph interface filtering issue: " +
                                  "interface properties appear in results but NOT in where clause " +
                                  "without IncludeInterface<T>() registration.",
                    ContentGraph = new
                    {
                        GatewayAddress = opts.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(opts.AppKey),
                        HasSingleKey = !string.IsNullOrWhiteSpace(opts.SingleKey),
                        IncludeInheritanceInContentType = opts.IncludeInheritanceInContentType
                    },
                    Issue = new
                    {
                        Symptom = "Property 'HideFromSearchResults' in where clause fails: \"The specified input object field does not exist.\"",
                        RootCause = "Custom interface properties are indexed on concrete types but NOT on the base Content type.",
                        Solution = "Register via IncludeInterface<IIndexableContent>() then query the IIndexableContent type directly.",
                        SchemaSync = "After registration + sync, re-download schema.graphql if using external code generation tools."
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-interface-filter/config",
                        "https://localhost:5009/util-api/custom-interface-filter/inspect-interfaces?contentId=5",
                        "https://localhost:5009/util-api/custom-interface-filter/introspect-schema?interfaceName=IIndexableContent",
                        "https://localhost:5009/util-api/custom-interface-filter/query-content-with-interface-field?fieldName=HideFromSearchResults&limit=5",
                        "https://localhost:5009/util-api/custom-interface-filter/register-interface",
                        "https://localhost:5009/util-api/custom-interface-filter/query-interface-type?interfaceName=IIndexableContent&limit=5",
                        "https://localhost:5009/util-api/custom-interface-filter/query-interface-with-filter?interfaceName=IIndexableContent&fieldName=HideFromSearchResults&fieldValue=false&limit=5",
                        "https://localhost:5009/util-api/custom-interface-filter/check-schema-for-type?typeName=IIndexableContent"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Inspects C# interfaces implemented by a content item's model type.
        /// Shows which custom interfaces are candidates for IncludeInterface registration.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/inspect-interfaces?contentId=5
        /// </summary>
        [HttpGet("inspect-interfaces")]
        public IActionResult InspectInterfaces([FromQuery] int contentId = 5)
        {
            try
            {
                if (!_contentLoader.TryGet<IContent>(new ContentReference(contentId), out var content))
                    return NotFound(new { Error = $"Content with ID {contentId} not found." });

                var contentType = _contentTypeRepository.Load(content.ContentTypeID);
                var modelType = contentType?.ModelType ?? content.GetType();

                var interfaces = modelType.GetInterfaces()
                    .Select(i => new
                    {
                        Name = i.Name,
                        FullName = i.FullName,
                        Properties = i.GetProperties()
                            .Select(p => new { p.Name, Type = p.PropertyType.Name })
                            .ToList(),
                        IsCustom = !i.Namespace?.StartsWith("EPiServer") == true &&
                                   !i.Namespace?.StartsWith("System") == true
                    })
                    .OrderByDescending(i => i.IsCustom)
                    .ToList();

                return Ok(new
                {
                    Step = "2 - Inspect C# Interfaces",
                    ContentId = contentId,
                    ContentName = content.Name,
                    ConcreteType = modelType.Name,
                    CustomInterfaces = interfaces.Where(i => i.IsCustom).ToList(),
                    AllInterfaces = interfaces,
                    Hint = "Custom interfaces with simple types (string, bool) are candidates for IncludeInterface<T>()."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Introspects the live Graph schema to check if an interface type exists.
        /// If null, IncludeInterface has not been registered or sync hasn't run.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/introspect-schema?interfaceName=IIndexableContent
        /// </summary>
        [HttpGet("introspect-schema")]
        public async Task<IActionResult> IntrospectSchema(
            [FromQuery] string interfaceName = "IIndexableContent")
        {
            try
            {
                var typeQuery = $@"{{ __type(name: ""{Esc(interfaceName)}"") {{ name kind fields {{ name type {{ name kind }} }} }} }}";
                var whereQuery = $@"{{ __type(name: ""{Esc(interfaceName)}WhereInput"") {{ name kind inputFields {{ name type {{ name kind }} }} }} }}";

                var typeResp = await _graphClient.QueryAsync(typeQuery, new { });
                var whereResp = await _graphClient.QueryAsync(whereQuery, new { });

                return Ok(new
                {
                    Step = "3 - Introspect Graph Schema",
                    InterfaceName = interfaceName,
                    TypeIntrospection = new { Query = typeQuery, Response = Parse(typeResp) },
                    WhereInputIntrospection = new { Query = whereQuery, Response = Parse(whereResp) },
                    Interpretation = "If __type returns null → interface not registered or sync not run."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Reproduces the FAILING query — using an interface property in the Content
        /// root where clause. Replicates: "The specified input object field does not exist."
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/query-content-with-interface-field?fieldName=HideFromSearchResults&amp;limit=5
        /// </summary>
        [HttpGet("query-content-with-interface-field")]
        public async Task<IActionResult> QueryContentWithInterfaceField(
            [FromQuery] string fieldName = "HideFromSearchResults",
            [FromQuery] int limit = 5)
        {
            try
            {
                var failingQuery = $@"{{ Content(limit: {limit}, where: {{ {fieldName}: {{ eq: false }} }}) {{ items {{ Name ContentType ContentLink {{ Id }} }} total }} }}";
                var concreteQuery = $@"{{ StandardPage(limit: {limit}) {{ items {{ Name ContentType ContentLink {{ Id }} {fieldName} }} total }} }}";

                var failResp = await SafeQuery(failingQuery);
                var concreteResp = await SafeQuery(concreteQuery);

                return Ok(new
                {
                    Step = "4 - Reproduce Failing Query (Content root + interface field in where)",
                    FailingApproach = new
                    {
                        Label = "FAILING: Filter by interface property on Content root",
                        GraphQLQuery = failingQuery,
                        ExpectedError = $"\"The specified input object field '{fieldName}' does not exist.\"",
                        Response = Parse(failResp)
                    },
                    WorkingApproach = new
                    {
                        Label = "WORKING: Property IS available on concrete type results",
                        GraphQLQuery = concreteQuery,
                        Response = Parse(concreteResp)
                    },
                    Solution = "Register interface with IncludeInterface<T>() → Step 5, then query → Step 6/7."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Registers IIndexableContent via ConventionRepository.IncludeInterface at runtime.
        /// After this, run the Graph Content Synchronization Job from CMS Admin.
        /// Note: In production, do this in an IInitializableModule.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/register-interface
        /// </summary>
        [HttpGet("register-interface")]
        public IActionResult RegisterInterface()
        {
            try
            {
                _conventionRepository.IncludeInterface<IIndexableContent>();

                return Ok(new
                {
                    Step = "5 - Register Interface (IncludeInterface<T>)",
                    Status = "Registered successfully",
                    InterfaceRegistered = "IIndexableContent",
                    Properties = typeof(IIndexableContent)
                        .GetProperties()
                        .Select(p => new { p.Name, Type = p.PropertyType.Name })
                        .ToList(),
                    NextSteps = new[]
                    {
                        "1. Run 'Optimizely Graph Content Synchronization Job' from CMS Admin",
                        "2. Call Step 3 (introspect-schema) to verify the type exists",
                        "3. Call Step 6/7 to query using interface type"
                    },
                    ProductionCode = @"
[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class GraphConventionsModule : IInitializableModule
{
    public void Initialize(InitializationEngine context)
    {
        var repo = context.Locate.Advanced.GetInstance<ConventionRepository>();
        repo.IncludeInterface<IIndexableContent>();
    }
    public void Uninitialize(InitializationEngine context) { }
}"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Queries Content Graph using the interface type as the GraphQL root.
        /// Works ONLY after IncludeInterface registration + Graph sync.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/query-interface-type?interfaceName=IIndexableContent&amp;limit=5
        /// </summary>
        [HttpGet("query-interface-type")]
        public async Task<IActionResult> QueryInterfaceType(
            [FromQuery] string interfaceName = "IIndexableContent",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{ {interfaceName}(limit: {limit}) {{ items {{ __typename Name ContentType ContentLink {{ Id }} HideFromSearchResults }} total }} }}";
                var resp = await SafeQuery(query);

                return Ok(new
                {
                    Step = "6 - Query Interface Type as Root",
                    InterfaceName = interfaceName,
                    GraphQLQuery = query,
                    Response = Parse(resp),
                    Note = "If error → ensure Step 5 (register) + Graph sync job completed."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Queries interface type WITH where clause filter — the final goal.
        /// Equivalent to Search&amp;Navigation .Filter(x =&gt; x.Field.Match(value)).
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/query-interface-with-filter?interfaceName=IIndexableContent&amp;fieldName=HideFromSearchResults&amp;fieldValue=false&amp;limit=5
        /// </summary>
        [HttpGet("query-interface-with-filter")]
        public async Task<IActionResult> QueryInterfaceWithFilter(
            [FromQuery] string interfaceName = "IIndexableContent",
            [FromQuery] string fieldName = "HideFromSearchResults",
            [FromQuery] string fieldValue = "false",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{ {interfaceName}(limit: {limit}, where: {{ {fieldName}: {{ eq: {fieldValue} }} }}) {{ items {{ __typename Name ContentType ContentLink {{ Id }} HideFromSearchResults }} total }} }}";
                var resp = await SafeQuery(query);

                return Ok(new
                {
                    Step = "7 - Query Interface with Where Clause Filter (THE GOAL)",
                    InterfaceName = interfaceName,
                    Filter = $"{fieldName} = {fieldValue}",
                    GraphQLQuery = query,
                    Response = Parse(resp),
                    SearchNavEquivalent = $"SearchClient.Instance.Search<{interfaceName}>().Filter(x => x.{fieldName}.Match({fieldValue})).GetResult();"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 8: Checks if a type exists as a root query field in the live schema.
        /// If missing, code generation tools will fail because the type is not in the downloaded schema.
        /// Sample usage: https://localhost:5009/util-api/custom-interface-filter/check-schema-for-type?typeName=IIndexableContent
        /// </summary>
        [HttpGet("check-schema-for-type")]
        public async Task<IActionResult> CheckSchemaForType(
            [FromQuery] string typeName = "IIndexableContent")
        {
            try
            {
                var rootQuery = @"{ __schema { queryType { fields { name } } } }";
                var rootResp = await _graphClient.QueryAsync(rootQuery, new { });

                bool typeExists = false;
                try
                {
                    using var doc = JsonDocument.Parse(rootResp);
                    typeExists = doc.RootElement
                        .GetProperty("data").GetProperty("__schema")
                        .GetProperty("queryType").GetProperty("fields")
                        .EnumerateArray()
                        .Any(f => f.GetProperty("name").GetString() == typeName);
                }
                catch { }

                return Ok(new
                {
                    Step = "8 - Check Schema for Type (Schema Sync Debug)",
                    TypeName = typeName,
                    ExistsAsRootQueryField = typeExists,
                    Diagnosis = typeExists
                        ? $"'{typeName}' EXISTS in live schema. If code generation still fails, re-download schema.graphql."
                        : $"'{typeName}' NOT in live schema. Register + sync first.",
                    Fix = new[]
                    {
                        "1. Register: conventionRepo.IncludeInterface<IIndexableContent>()",
                        "2. Run Graph Full Sync job",
                        "3. Re-download schema from the Content Graph endpoint",
                        "4. Clean build: delete obj/bin then dotnet build"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Helpers

        private async Task<string> SafeQuery(string query)
        {
            try { return await _graphClient.QueryAsync(query, new { }); }
            catch (Exception ex) { return JsonSerializer.Serialize(new { Error = ex.Message }); }
        }

        private static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new { IsEmpty = true };
            try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
            catch { return new { IsJson = false, Raw = json }; }
        }

        private static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");

        #endregion
    }
}
