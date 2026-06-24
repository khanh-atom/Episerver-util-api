using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to reproduce the Content Graph stored template cache bug.
    /// When using HMAC/Basic auth with template caching (stored=true + cg-stored-query: template),
    /// the template cache key does not include cg-roles or cg-username headers.
    /// This means the first requester's RBAC filters are baked into the cached template
    /// and reused for all subsequent requests regardless of their roles.
    ///
    /// Steps to reproduce:
    /// 1. GET /config — Show Graph config and all sample URLs
    /// 2. GET /query-with-role — Query with HMAC auth + cg-roles + template caching (creates the cached template)
    /// 3. GET /query-with-different-role — Same query but different cg-roles (should get different results, but gets cached template)
    /// 4. GET /query-without-cache — Same query WITHOUT template caching (correct baseline)
    /// 5. GET /compare-roles — Side-by-side comparison showing the bug
    /// 6. GET /query-single-key — Query with SingleKey auth (no RBAC) for baseline comparison
    /// </summary>
    [ApiController]
    [Route("util-api/custom-stored-template-cache")]
    public class CustomStoredTemplateCacheController : ControllerBase
    {
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IHttpClientFactory _httpClientFactory;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public CustomStoredTemplateCacheController(
            IOptions<QueryOptions> queryOptions,
            IHttpClientFactory httpClientFactory)
        {
            _queryOptions = queryOptions;
            _httpClientFactory = httpClientFactory;
        }

        #region Helpers

        private string GatewayBase => (_queryOptions.Value.GatewayAddress ?? "https://staging.cg.optimizely.com").TrimEnd('/');
        private string AppKey => _queryOptions.Value.AppKey;
        private string Secret => _queryOptions.Value.Secret;
        private string SingleKey => _queryOptions.Value.SingleKey;

        /// <summary>
        /// Creates an HttpClient with Basic auth (AppKey:Secret) for HMAC-equivalent access.
        /// Optionally sets cg-roles, cg-username, and cg-stored-query headers.
        /// </summary>
        private HttpClient CreateHmacClient(string roles = null, string username = null, bool enableTemplateCache = false)
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(GatewayBase);

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AppKey}:{Secret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            if (!string.IsNullOrWhiteSpace(roles))
                client.DefaultRequestHeaders.TryAddWithoutValidation("cg-roles", roles);

            if (!string.IsNullOrWhiteSpace(username))
                client.DefaultRequestHeaders.TryAddWithoutValidation("cg-username", username);

            if (enableTemplateCache)
                client.DefaultRequestHeaders.TryAddWithoutValidation("cg-stored-query", "template");

            return client;
        }

        /// <summary>
        /// Sends a GraphQL query to Content Graph and returns the raw response + headers.
        /// </summary>
        private async Task<(int statusCode, JsonElement? body, Dictionary<string, string> responseHeaders)> SendGraphQuery(
            HttpClient client, string graphqlQuery, bool storedQueryParam = false)
        {
            var path = storedQueryParam ? "/content/v2?stored=true" : "/content/v2";
            var requestBody = JsonSerializer.Serialize(new { query = graphqlQuery }, _jsonOptions);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = content
            });

            var body = await resp.Content.ReadAsStringAsync();
            var parsed = string.IsNullOrWhiteSpace(body) ? (JsonElement?)null : JsonDocument.Parse(body).RootElement.Clone();

            var headers = new Dictionary<string, string>();
            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(", ", h.Value);

            return ((int)resp.StatusCode, parsed, headers);
        }

        private static object TryParseJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
                return new { IsEmpty = true, Raw = rawResponse };
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

        #endregion

        /// <summary>
        /// Step 0: Shows current Content Graph config and all sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/config
        /// </summary>
        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            try
            {
                return Ok(new
                {
                    Step = "0 — Configuration & Sample URLs",
                    Description = "Reproduces the Content Graph stored template cache bug where " +
                                  "the cache key ignores auth context (cg-roles/cg-username). " +
                                  "Two requests with different roles share a cached template, " +
                                  "causing cross-role content leakage.",
                    ContentGraph = new
                    {
                        GatewayAddress = GatewayBase,
                        HasAppKey = !string.IsNullOrWhiteSpace(AppKey),
                        HasSecret = !string.IsNullOrWhiteSpace(Secret),
                        HasSingleKey = !string.IsNullOrWhiteSpace(SingleKey),
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-stored-template-cache/config",
                        "https://localhost:5009/util-api/custom-stored-template-cache/query-with-role?roles=Administrators&limit=3",
                        "https://localhost:5009/util-api/custom-stored-template-cache/query-with-different-role?roles=Everyone&limit=3",
                        "https://localhost:5009/util-api/custom-stored-template-cache/query-without-cache?roles=Everyone&limit=3",
                        "https://localhost:5009/util-api/custom-stored-template-cache/compare-roles?roleA=Administrators&roleB=Everyone&limit=5",
                        "https://localhost:5009/util-api/custom-stored-template-cache/query-single-key?limit=5",
                    },
                    AclReproduction = new
                    {
                        Description = "Cleaner bug reproduction using ACL changes on a single page",
                        Steps = new[]
                        {
                            "Step A1: Call /repro-query-page?contentId=15 — queries a specific page with cg-roles=Everyone + template caching. Expect MISS and the page appears in results.",
                            "Step A2: In CMS, go to that page's Access Rights → REMOVE the 'Everyone' role → Publish the page.",
                            "Step A3: Wait for Graph event indexing (~10-30s) or run the Graph sync job.",
                            "Step A4: Call /repro-verify-after-acl-change?contentId=15 — compares cached vs non-cached. Cached still returns the page (BUG), non-cached correctly hides it.",
                        },
                        SampleUrls = new[]
                        {
                            "https://localhost:5009/util-api/custom-stored-template-cache/repro-query-page?contentId=15",
                            "https://localhost:5009/util-api/custom-stored-template-cache/repro-verify-after-acl-change?contentId=15",
                        }
                    },
                    HowToReproduce = new[]
                    {
                        "--- Approach 1: ACL change on a single page (recommended) ---",
                        "Step A1: Call /repro-query-page?contentId=15 — creates cached template with Everyone having access",
                        "Step A2: In CMS, remove Everyone from that page's ACL and publish",
                        "Step A3: Wait for Graph sync (~10-30s)",
                        "Step A4: Call /repro-verify-after-acl-change?contentId=15 — cached still shows page, non-cached hides it",
                        "",
                        "--- Approach 2: Compare two roles side by side ---",
                        "Step B1: Call /query-with-role?roles=Administrators — creates the cached template with Admin RBAC filters",
                        "Step B2: Call /query-with-different-role?roles=Everyone — should get different results but may get Admin's cached template",
                        "Step B3: Call /query-without-cache?roles=Everyone — baseline without caching, shows correct results",
                        "Step B4: Call /compare-roles — side-by-side comparison exposing the discrepancy",
                        "Step B5: Call /query-single-key — SingleKey baseline (public content only, no RBAC)",
                    },
                    BugExplanation = new
                    {
                        Issue = "Template cache key = hash(query_text + variable_structure). " +
                                "Auth scheme, cg-roles, and cg-username are NOT part of the hash.",
                        Impact = "RBAC system filters (filterRBAC) are baked into the ES body at template creation " +
                                 "using the first requester's auth context. All subsequent requesters reuse that ES body.",
                        SecurityRisk = "A user with restricted roles could receive content meant for Administrators, or vice versa."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Query Content Graph with HMAC auth + cg-roles + template caching enabled.
        /// This is the request that CREATES the cached template. The RBAC filters for the given role
        /// are baked into the cached Elasticsearch body.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/query-with-role?roles=Administrators&amp;limit=3
        /// </summary>
        [HttpGet("query-with-role")]
        public async Task<IActionResult> QueryWithRole(
            [FromQuery] string roles = "Administrators",
            [FromQuery] string username = "admin",
            [FromQuery] string contentType = "Content",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{
  {contentType}(limit: {limit}) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
      Language {{ Name }}
    }}
    total
  }}
}}";

                using var client = CreateHmacClient(roles: roles, username: username, enableTemplateCache: true);
                var (statusCode, body, headers) = await SendGraphQuery(client, query, storedQueryParam: true);

                return Ok(new
                {
                    Step = "1 — Query with HMAC + cg-roles + Template Caching",
                    Description = $"Sends query with cg-roles='{roles}', cg-username='{username}', " +
                                  "stored=true, cg-stored-query=template. " +
                                  "This creates the cached template with RBAC filters for this role.",
                    RequestDetails = new
                    {
                        Auth = "Basic (AppKey:Secret)",
                        CgRoles = roles,
                        CgUsername = username,
                        StoredQueryParam = "stored=true",
                        CgStoredQueryHeader = "template",
                    },
                    GraphQLQuery = query,
                    HttpStatus = statusCode,
                    ResponseHeaders = headers,
                    Response = body,
                    Note = "Check x-cache-hit header. On first call = MISS (template created). " +
                           "On second call with SAME roles = HIT. " +
                           "On call with DIFFERENT roles = also HIT (BUG — should be MISS or separate template)."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Query with a DIFFERENT role but same query text + template caching.
        /// Due to the bug, this returns the cached template from Step 1 — filtered by the WRONG role.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/query-with-different-role?roles=Everyone&amp;limit=3
        /// </summary>
        [HttpGet("query-with-different-role")]
        public async Task<IActionResult> QueryWithDifferentRole(
            [FromQuery] string roles = "Everyone",
            [FromQuery] string username = "anonymous",
            [FromQuery] string contentType = "Content",
            [FromQuery] int limit = 5)
        {
            try
            {
                // Same query text as Step 1 — so it matches the same template hash
                var query = $@"{{
  {contentType}(limit: {limit}) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
      Language {{ Name }}
    }}
    total
  }}
}}";

                using var client = CreateHmacClient(roles: roles, username: username, enableTemplateCache: true);
                var (statusCode, body, headers) = await SendGraphQuery(client, query, storedQueryParam: true);

                return Ok(new
                {
                    Step = "2 — Query with DIFFERENT role + Template Caching (BUG)",
                    Description = $"Sends the SAME query with cg-roles='{roles}', cg-username='{username}'. " +
                                  "Because the template cache key ignores roles, this reuses the cached template " +
                                  "from Step 1 — returning results filtered by the WRONG role's RBAC rules.",
                    RequestDetails = new
                    {
                        Auth = "Basic (AppKey:Secret)",
                        CgRoles = roles,
                        CgUsername = username,
                        StoredQueryParam = "stored=true",
                        CgStoredQueryHeader = "template",
                    },
                    GraphQLQuery = query,
                    HttpStatus = statusCode,
                    ResponseHeaders = headers,
                    Response = body,
                    BugIndicator = "If x-cache-hit=true and the results are identical to Step 1 despite different roles, " +
                                   "the bug is confirmed. The cached template's RBAC filters belong to Step 1's role."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Query with HMAC auth + cg-roles but WITHOUT template caching.
        /// This is the correct baseline — each request gets its own RBAC filtering.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/query-without-cache?roles=Everyone&amp;limit=3
        /// </summary>
        [HttpGet("query-without-cache")]
        public async Task<IActionResult> QueryWithoutCache(
            [FromQuery] string roles = "Everyone",
            [FromQuery] string username = "anonymous",
            [FromQuery] string contentType = "Content",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{
  {contentType}(limit: {limit}) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
      Language {{ Name }}
    }}
    total
  }}
}}";

                // No template caching — no stored=true, no cg-stored-query header
                using var client = CreateHmacClient(roles: roles, username: username, enableTemplateCache: false);
                var (statusCode, body, headers) = await SendGraphQuery(client, query, storedQueryParam: false);

                return Ok(new
                {
                    Step = "3 — Query WITHOUT Template Caching (Correct Baseline)",
                    Description = $"Sends the same query with cg-roles='{roles}' but WITHOUT stored=true or cg-stored-query header. " +
                                  "This forces a fresh query translation with correct RBAC filtering for this role.",
                    RequestDetails = new
                    {
                        Auth = "Basic (AppKey:Secret)",
                        CgRoles = roles,
                        CgUsername = username,
                        StoredQueryParam = "NONE",
                        CgStoredQueryHeader = "NONE",
                    },
                    GraphQLQuery = query,
                    HttpStatus = statusCode,
                    ResponseHeaders = headers,
                    Response = body,
                    Note = "Compare these results with Step 2. If they differ, the bug is confirmed: " +
                           "template caching returns wrong results for this role."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Side-by-side comparison of two different roles using template caching vs without.
        /// Sends 4 requests: RoleA+cache, RoleB+cache, RoleA-no-cache, RoleB-no-cache.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/compare-roles?roleA=Administrators&amp;roleB=Everyone&amp;limit=5
        /// </summary>
        [HttpGet("compare-roles")]
        public async Task<IActionResult> CompareRoles(
            [FromQuery] string roleA = "Administrators",
            [FromQuery] string roleB = "Everyone",
            [FromQuery] string contentType = "Content",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{
  {contentType}(limit: {limit}) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                // 1. RoleA WITH template cache
                using var clientA1 = CreateHmacClient(roles: roleA, username: "userA", enableTemplateCache: true);
                var (scA1, bodyA1, headersA1) = await SendGraphQuery(clientA1, query, storedQueryParam: true);

                // 2. RoleB WITH template cache (same query text = same template hash)
                using var clientB1 = CreateHmacClient(roles: roleB, username: "userB", enableTemplateCache: true);
                var (scB1, bodyB1, headersB1) = await SendGraphQuery(clientB1, query, storedQueryParam: true);

                // 3. RoleA WITHOUT template cache (correct baseline)
                using var clientA2 = CreateHmacClient(roles: roleA, username: "userA", enableTemplateCache: false);
                var (scA2, bodyA2, headersA2) = await SendGraphQuery(clientA2, query, storedQueryParam: false);

                // 4. RoleB WITHOUT template cache (correct baseline)
                using var clientB2 = CreateHmacClient(roles: roleB, username: "userB", enableTemplateCache: false);
                var (scB2, bodyB2, headersB2) = await SendGraphQuery(clientB2, query, storedQueryParam: false);

                // Compare totals
                int? totalA1 = ExtractTotal(bodyA1, contentType);
                int? totalB1 = ExtractTotal(bodyB1, contentType);
                int? totalA2 = ExtractTotal(bodyA2, contentType);
                int? totalB2 = ExtractTotal(bodyB2, contentType);

                var cachedResultsMatch = bodyA1?.ToString() == bodyB1?.ToString();
                var baselineResultsDiffer = bodyA2?.ToString() != bodyB2?.ToString();
                var bugDetected = cachedResultsMatch && baselineResultsDiffer;

                return Ok(new
                {
                    Step = "4 — Side-by-Side Role Comparison",
                    Description = $"Compares role '{roleA}' vs '{roleB}' with and without template caching.",
                    GraphQLQuery = query,

                    WithTemplateCaching = new
                    {
                        RoleA = new { Role = roleA, Total = totalA1, HttpStatus = scA1, CacheHeaders = FilterCacheHeaders(headersA1), Response = bodyA1 },
                        RoleB = new { Role = roleB, Total = totalB1, HttpStatus = scB1, CacheHeaders = FilterCacheHeaders(headersB1), Response = bodyB1 },
                        ResultsIdentical = cachedResultsMatch,
                    },
                    WithoutTemplateCaching = new
                    {
                        RoleA = new { Role = roleA, Total = totalA2, HttpStatus = scA2, Response = bodyA2 },
                        RoleB = new { Role = roleB, Total = totalB2, HttpStatus = scB2, Response = bodyB2 },
                        ResultsDiffer = baselineResultsDiffer,
                    },
                    Analysis = new
                    {
                        BugDetected = bugDetected,
                        Explanation = bugDetected
                            ? $"BUG CONFIRMED: With template caching, both roles get identical results " +
                              $"(total={totalA1}). Without caching, they correctly differ " +
                              $"(roleA={totalA2}, roleB={totalB2}). " +
                              "The cached template's RBAC filters from the first request are reused for the second."
                            : cachedResultsMatch && !baselineResultsDiffer
                                ? "Both roles return the same results with and without caching. " +
                                  "This may be expected if both roles have the same content visibility. " +
                                  "Try roles with clearly different access levels."
                                : "Results look correct — roles get different cached templates. " +
                                  "The bug may have been fixed or the template was invalidated between requests."
                    },
                    Workaround = "Remove stored=true query param and cg-stored-query:template header " +
                                 "when using HMAC/Basic auth with cg-roles."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Query with SingleKey auth (public content only, no RBAC).
        /// Provides a baseline of what public/Everyone-visible content looks like.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/query-single-key?limit=5
        /// </summary>
        [HttpGet("query-single-key")]
        public async Task<IActionResult> QuerySingleKey(
            [FromQuery] string contentType = "Content",
            [FromQuery] int limit = 5)
        {
            try
            {
                var query = $@"{{
  {contentType}(limit: {limit}) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
      Language {{ Name }}
    }}
    total
  }}
}}";

                using var httpClient = _httpClientFactory.CreateClient();
                var url = $"{GatewayBase}/content/v2?auth={SingleKey}";
                var requestBody = JsonSerializer.Serialize(new { query }, _jsonOptions);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                var parsed = string.IsNullOrWhiteSpace(responseBody) ? (JsonElement?)null : JsonDocument.Parse(responseBody).RootElement.Clone();

                var headers = new Dictionary<string, string>();
                foreach (var h in response.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);

                return Ok(new
                {
                    Step = "5 — Query with SingleKey (Public Content Baseline)",
                    Description = "Queries with SingleKey auth — returns only content visible to the Everyone group. " +
                                  "No RBAC filtering. This is the public content baseline.",
                    RequestDetails = new
                    {
                        Auth = "SingleKey (query param)",
                        CgRoles = "NONE (SingleKey does not support RBAC)",
                    },
                    GraphQLQuery = query,
                    HttpStatus = (int)response.StatusCode,
                    ResponseHeaders = headers,
                    Response = parsed,
                    Note = "Compare total here with the HMAC results. " +
                           "HMAC with Administrators role should see MORE content than SingleKey. " +
                           "HMAC with Everyone role should see the SAME content as SingleKey."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Private Helpers

        private static int? ExtractTotal(JsonElement? body, string contentType)
        {
            try
            {
                if (body?.TryGetProperty("data", out var data) == true &&
                    data.TryGetProperty(contentType, out var node) &&
                    node.TryGetProperty("total", out var total))
                {
                    return total.GetInt32();
                }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, string> FilterCacheHeaders(Dictionary<string, string> headers)
        {
            var cacheKeys = new[] { "x-cache-hit", "x-cache", "x-stored-query", "x-correlation-id", "cache-control" };
            return headers
                .Where(h => cacheKeys.Any(k => h.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(h => h.Key, h => h.Value);
        }

        #endregion

        #region ACL-based Reproduction

        /// <summary>
        /// Repro Step A1: Query a specific page by ContentLink.Id with cg-roles=Everyone + template caching.
        /// This creates the cached template while the page is still accessible to Everyone.
        /// After this, go to CMS and remove the Everyone role from the page's ACL, then publish and re-sync.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/repro-query-page?contentId=15
        /// </summary>
        [HttpGet("repro-query-page")]
        public async Task<IActionResult> ReproQueryPage(
            [FromQuery] int contentId = 15,
            [FromQuery] string roles = "Everyone")
        {
            try
            {
                // Use a very specific query so the template is unique to this test
                var query = $@"{{
  Content(
    where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}
  ) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                using var client = CreateHmacClient(roles: roles, username: "testuser", enableTemplateCache: true);
                var (statusCode, body, headers) = await SendGraphQuery(client, query, storedQueryParam: true);

                var total = ExtractTotal(body, "Content");

                return Ok(new
                {
                    Step = "Repro A1 — Query page with Everyone role + Template Caching",
                    Description = $"Queries content ID {contentId} with cg-roles='{roles}' and template caching enabled. " +
                                  "This creates the cached template while the page is accessible to Everyone.",
                    RequestDetails = new
                    {
                        Auth = "Basic (AppKey:Secret)",
                        CgRoles = roles,
                        StoredQueryParam = "stored=true",
                        CgStoredQueryHeader = "template",
                    },
                    GraphQLQuery = query,
                    HttpStatus = statusCode,
                    Total = total,
                    ResponseHeaders = FilterCacheHeaders(headers),
                    Response = body,
                    NextSteps = new[]
                    {
                        $"1. Verify total={total} — the page should appear in results (total >= 1)",
                        $"2. Go to CMS → navigate to content ID {contentId}",
                        "3. Edit the page's Access Rights → REMOVE the 'Everyone' role",
                        "4. Publish the page",
                        "5. Wait ~10-30s for Graph event indexing (or run the sync job)",
                        $"6. Call /repro-verify-after-acl-change?contentId={contentId}",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Repro Step A4: After removing Everyone from the page's ACL and re-syncing,
        /// compare cached vs non-cached results.
        /// Cached query should still return the page (BUG), non-cached should correctly hide it.
        /// Sample usage: https://localhost:5009/util-api/custom-stored-template-cache/repro-verify-after-acl-change?contentId=15
        /// </summary>
        [HttpGet("repro-verify-after-acl-change")]
        public async Task<IActionResult> ReproVerifyAfterAclChange(
            [FromQuery] int contentId = 15,
            [FromQuery] string roles = "Everyone")
        {
            try
            {
                // Same query as repro-query-page — must match exactly for template HIT
                var query = $@"{{
  Content(
    where: {{ ContentLink: {{ Id: {{ eq: {contentId} }} }} }}
  ) {{
    items {{
      Name
      ContentType
      Status
      ContentLink {{ Id }}
    }}
    total
  }}
}}";

                // 1. WITH template caching (should reuse cached template = BUG)
                using var cachedClient = CreateHmacClient(roles: roles, username: "testuser", enableTemplateCache: true);
                var (scCached, bodyCached, headersCached) = await SendGraphQuery(cachedClient, query, storedQueryParam: true);
                var totalCached = ExtractTotal(bodyCached, "Content");

                // 2. WITHOUT template caching (should show correct RBAC filtering)
                using var noCacheClient = CreateHmacClient(roles: roles, username: "testuser", enableTemplateCache: false);
                var (scNoCache, bodyNoCache, headersNoCache) = await SendGraphQuery(noCacheClient, query, storedQueryParam: false);
                var totalNoCache = ExtractTotal(bodyNoCache, "Content");

                var bugDetected = totalCached > 0 && totalNoCache == 0;

                return Ok(new
                {
                    Step = "Repro A4 — Verify after ACL change (Cached vs Non-Cached)",
                    Description = $"After removing '{roles}' from content ID {contentId}'s ACL and re-syncing, " +
                                  "compares cached template results vs fresh (non-cached) results.",

                    WithTemplateCaching = new
                    {
                        Label = "CACHED — uses stored template from before ACL change",
                        CgRoles = roles,
                        Total = totalCached,
                        HttpStatus = scCached,
                        CacheHeaders = FilterCacheHeaders(headersCached),
                        Response = bodyCached,
                    },
                    WithoutTemplateCaching = new
                    {
                        Label = "NON-CACHED — fresh query with correct RBAC filtering",
                        CgRoles = roles,
                        Total = totalNoCache,
                        HttpStatus = scNoCache,
                        Response = bodyNoCache,
                    },

                    Analysis = new
                    {
                        BugDetected = bugDetected,
                        CachedTotal = totalCached,
                        NonCachedTotal = totalNoCache,
                        Explanation = bugDetected
                            ? $"BUG CONFIRMED: Cached query returns {totalCached} result(s) for content ID {contentId}, " +
                              $"but non-cached query returns {totalNoCache}. " +
                              "The cached template still has the old RBAC filters from BEFORE the ACL change. " +
                              $"User with role '{roles}' should NOT see this content anymore, but the cached template serves stale access rules."
                            : totalCached == 0 && totalNoCache == 0
                                ? $"Both queries return 0. The ACL change was applied and the template may have been invalidated. " +
                                  "Try again — the template cache may have expired."
                                : totalCached > 0 && totalNoCache > 0
                                    ? $"Both queries return results (cached={totalCached}, non-cached={totalNoCache}). " +
                                      "The ACL change may not have been synced to Graph yet. Wait and try again, or run the Graph sync job."
                                    : "Unexpected result pattern. Check the responses for details."
                    },
                    Workaround = "Do NOT use stored=true + cg-stored-query:template when using HMAC/Basic auth with cg-roles headers."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #endregion
    }
}
