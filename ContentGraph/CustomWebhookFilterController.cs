using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to replicate the Graph webhook status filter bug using the REAL Content Graph API.
    /// Demonstrates that webhook filters do substring matching on docId instead of actual status field comparison.
    /// Base route: util-api/custom-webhook-filter
    /// </summary>
    [ApiController]
    [Route("util-api/custom-webhook-filter")]
    public class CustomWebhookFilterController : ControllerBase
    {
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        // Persisted state for the webhook receiver log
        private static readonly List<WebhookReceivedEvent> _receivedWebhookEvents = new();
        private static string _lastRegisteredWebhookId;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public CustomWebhookFilterController(
            IOptions<QueryOptions> queryOptions,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _queryOptions = queryOptions;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        #region Helpers

        private HttpClient CreateGraphClient()
        {
            var client = _httpClientFactory.CreateClient();
            var appKey = _queryOptions.Value.AppKey;
            var secret = _queryOptions.Value.Secret;
            var gateway = _queryOptions.Value.GatewayAddress ?? "https://staging.cg.optimizely.com";
            client.BaseAddress = new Uri(gateway.TrimEnd('/'));
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{appKey}:{secret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return client;
        }

        private async Task<JsonElement?> GraphGet(HttpClient client, string path)
        {
            var resp = await client.GetAsync(path);
            var body = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body).RootElement.Clone();
        }

        private async Task<(int statusCode, JsonElement? body)> GraphPost(HttpClient client, string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(path, content);
            var body = await resp.Content.ReadAsStringAsync();
            var parsed = string.IsNullOrWhiteSpace(body) ? (JsonElement?)null : JsonDocument.Parse(body).RootElement.Clone();
            return ((int)resp.StatusCode, parsed);
        }

        private async Task<(int statusCode, JsonElement? body)> GraphDelete(HttpClient client, string path)
        {
            var resp = await client.DeleteAsync(path);
            var body = await resp.Content.ReadAsStringAsync();
            var parsed = string.IsNullOrWhiteSpace(body) ? (JsonElement?)null : JsonDocument.Parse(body).RootElement.Clone();
            return ((int)resp.StatusCode, parsed);
        }

        private async Task<(int statusCode, string body)> GraphPut(HttpClient client, string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PutAsync(path, content);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, body);
        }

        #endregion

        /// <summary>
        /// Shows all available endpoints and current Graph config.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                return Ok(new
                {
                    Description = "Graph Webhook Status Filter — Real Content Graph API reproduction",
                    Config = new
                    {
                        GatewayAddress = _queryOptions.Value.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.AppKey),
                        HasSecret = !string.IsNullOrWhiteSpace(_queryOptions.Value.Secret),
                        HasSingleKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.SingleKey),
                    },
                    Steps = new[]
                    {
                        "Step 1: GET /list-webhooks — List all currently registered webhooks on this Graph account",
                        "Step 2: GET /register-webhook?status=Published — Register a webhook with status filter via real Graph API",
                        "Step 3: GET /register-webhook-no-filter — Register a webhook WITHOUT any filter (catch-all)",
                        "Step 4: GET /query-content?limit=5 — Query real content from Graph to get existing docIds",
                        "Step 5: GET /show-received-events — Show webhook events received by this CMS instance",
                        "Step 6: GET /delete-webhook?id=xxx — Delete a specific webhook registration",
                        "Step 7: GET /cleanup-webhooks — Delete all webhooks registered by this controller",
                        "Step 8: GET /analyze-docid-status — Analyze real content docIds to show the status suffix pattern",
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-webhook-filter/list-webhooks",
                        "https://localhost:5009/util-api/custom-webhook-filter/register-webhook?status=Published",
                        "https://localhost:5009/util-api/custom-webhook-filter/register-webhook-no-filter",
                        "https://localhost:5009/util-api/custom-webhook-filter/query-content?limit=5",
                        "https://localhost:5009/util-api/custom-webhook-filter/show-received-events",
                        "https://localhost:5009/util-api/custom-webhook-filter/delete-webhook?id=YOUR_WEBHOOK_ID",
                        "https://localhost:5009/util-api/custom-webhook-filter/cleanup-webhooks",
                        "https://localhost:5009/util-api/custom-webhook-filter/analyze-docid-status",
                    },
                    ReceivedEvents = _receivedWebhookEvents.Count,
                    LastRegisteredWebhookId = _lastRegisteredWebhookId
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: List all currently registered webhooks on this Graph account via the real Graph API.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/list-webhooks
        /// </summary>
        [HttpGet("list-webhooks")]
        public async Task<IActionResult> ListWebhooks()
        {
            try
            {
                using var client = CreateGraphClient();
                var result = await GraphGet(client, "/api/webhooks");

                return Ok(new
                {
                    Step = "1 — List Webhooks (Real Graph API)",
                    Endpoint = $"{_queryOptions.Value.GatewayAddress}api/webhooks",
                    Webhooks = result,
                    Message = "These are REAL webhook registrations on your Graph account."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Register a webhook WITH a status filter via the real Graph API.
        /// This is how customers register webhooks — and where the bug manifests.
        /// The filter { "status": { "eq": "Published" } } does substring matching on docId, not actual status.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/register-webhook?status=Published
        /// </summary>
        [HttpGet("register-webhook")]
        public async Task<IActionResult> RegisterWebhook(
            [FromQuery] string status = "Published",
            [FromQuery] string callbackUrl = null)
        {
            try
            {
                using var client = CreateGraphClient();

                // Default callback URL: this CMS instance's webhook endpoint
                var url = callbackUrl ?? $"https://localhost:5009/util-api/custom-webhook-filter/webhook-receiver";

                var registration = new
                {
                    request = new { url, method = "POST" },
                    topics = new[] { "*.*" },
                    filters = new[]
                    {
                        new { status = new { eq = status } }
                    }
                };

                var (statusCode, body) = await GraphPost(client, "/api/webhooks", registration);
                if (body?.ValueKind == JsonValueKind.Object && body.Value.TryGetProperty("id", out var idProp))
                {
                    _lastRegisteredWebhookId = idProp.GetString();
                }

                return Ok(new
                {
                    Step = "2 — Register Webhook WITH Status Filter (Real Graph API)",
                    RegistrationPayload = registration,
                    HttpStatus = statusCode,
                    Response = body,
                    RegisteredId = _lastRegisteredWebhookId,
                    BugExplanation = new
                    {
                        FilterSent = $"{{ \"status\": {{ \"eq\": \"{status}\" }} }}",
                        WhatGraphActuallyDoes = $"Checks if docId.Contains(\"{status}\") — substring match on the _id field",
                        WhatItShouldDo = $"Check if document.status == \"{status}\" — actual field comparison",
                        Result = "Draft saves on published pages will trigger this webhook because docId contains '_Published'"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Register a webhook WITHOUT any filter (catch-all) via the real Graph API.
        /// This is the recommended workaround — receive all events and filter in your own code.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/register-webhook-no-filter
        /// </summary>
        [HttpGet("register-webhook-no-filter")]
        public async Task<IActionResult> RegisterWebhookNoFilter(
            [FromQuery] string callbackUrl = null)
        {
            try
            {
                using var client = CreateGraphClient();
                var url = callbackUrl ?? $"https://localhost:5009/util-api/custom-webhook-filter/webhook-receiver";

                var registration = new
                {
                    request = new { url, method = "POST" },
                    topics = new[] { "*.*" }
                };

                var (statusCode, body) = await GraphPost(client, "/api/webhooks", registration);
                if (body?.ValueKind == JsonValueKind.Object && body.Value.TryGetProperty("id", out var idProp))
                {
                    _lastRegisteredWebhookId = idProp.GetString();
                }

                return Ok(new
                {
                    Step = "3 — Register Webhook WITHOUT Filter (Workaround)",
                    RegistrationPayload = registration,
                    HttpStatus = statusCode,
                    Response = body,
                    RegisteredId = _lastRegisteredWebhookId,
                    Workaround = "With no filter, you receive ALL events. Filter draft vs published in your own webhook handler by inspecting the docId suffix or changeset field."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Query real content from Graph to see existing documents and their IDs.
        /// Shows the docId pattern ({guid}_{lang}_{status}) that causes the filter issue.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/query-content?limit=5
        /// </summary>
        [HttpGet("query-content")]
        public async Task<IActionResult> QueryContent(
            [FromQuery] int limit = 5,
            [FromQuery] string contentType = "Content")
        {
            try
            {
                var singleKey = _queryOptions.Value.SingleKey;
                var gateway = _queryOptions.Value.GatewayAddress?.TrimEnd('/') ?? "https://staging.cg.optimizely.com";

                var graphqlQuery = $"{{ {contentType}(limit: {limit}) {{ total items {{ ContentType Status Name Url }} }} }}";

                using var httpClient = new HttpClient();
                var url = $"{gateway}/content/v2?auth={singleKey}";
                var requestBody = JsonSerializer.Serialize(new { query = graphqlQuery });
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                var parsed = JsonDocument.Parse(responseBody).RootElement.Clone();

                return Ok(new
                {
                    Step = "4 — Query Real Content from Graph",
                    GraphQLQuery = graphqlQuery,
                    HttpStatus = (int)response.StatusCode,
                    Response = parsed,
                    Note = "Look at the _id field — it contains the status suffix (_Published, _Draft) that the webhook filter matches against"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Webhook receiver endpoint — Graph calls this URL when a webhook event is triggered.
        /// Logs all received events for inspection via /show-received-events.
        /// Sample usage: This endpoint is called BY Graph, not by the user directly.
        /// </summary>
        [HttpPost("webhook-receiver")]
        public IActionResult WebhookReceiver([FromBody] JsonElement payload)
        {
            try
            {
                var received = new WebhookReceivedEvent
                {
                    ReceivedAt = DateTime.UtcNow,
                    RawPayload = payload.Clone(),
                };

                // Extract fields from payload
                if (payload.TryGetProperty("type", out var typeProp))
                {
                    if (typeProp.TryGetProperty("subject", out var subj))
                        received.Subject = subj.GetString();
                    if (typeProp.TryGetProperty("action", out var act))
                        received.Action = act.GetString();
                }

                if (payload.TryGetProperty("data", out var dataProp))
                {
                    if (dataProp.TryGetProperty("docId", out var docId))
                    {
                        received.DocId = docId.GetString();
                        received.ExtractedStatus = ExtractStatusFromDocId(received.DocId);
                    }
                    if (dataProp.TryGetProperty("items", out var items))
                    {
                        received.IsBulk = true;
                        received.ItemCount = items.EnumerateObject().Count();
                    }
                }

                _receivedWebhookEvents.Add(received);

                return Ok(new { received = true, eventNumber = _receivedWebhookEvents.Count });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Show all webhook events received by this CMS instance.
        /// After registering a webhook and editing content in CMS, check here to see what events fired.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/show-received-events
        /// </summary>
        [HttpGet("show-received-events")]
        public IActionResult ShowReceivedEvents([FromQuery] bool clear = false)
        {
            try
            {
                var events = _receivedWebhookEvents.ToList();
                if (clear) _receivedWebhookEvents.Clear();

                var draftFires = events.Where(e => e.ExtractedStatus != null &&
                    !e.ExtractedStatus.Equals("Published", StringComparison.OrdinalIgnoreCase) &&
                    e.DocId?.Contains("Published", StringComparison.OrdinalIgnoreCase) == true).ToList();

                return Ok(new
                {
                    Step = "5 — Received Webhook Events",
                    TotalReceived = events.Count,
                    Events = events.Select(e => new
                    {
                        e.ReceivedAt,
                        e.Subject,
                        e.Action,
                        e.DocId,
                        e.ExtractedStatus,
                        e.IsBulk,
                        e.ItemCount,
                        ContainsPublishedInId = e.DocId?.Contains("Published", StringComparison.OrdinalIgnoreCase),
                        e.RawPayload,
                    }),
                    Analysis = new
                    {
                        TotalEvents = events.Count,
                        DraftEventsWithPublishedId = draftFires.Count,
                        BugTriggered = draftFires.Count > 0,
                        Explanation = draftFires.Count > 0
                            ? $"{draftFires.Count} event(s) have Draft status but docId contains 'Published' — these would incorrectly match a status=Published filter"
                            : "No draft-with-Published-id events detected yet. Edit a published page as draft in CMS to trigger the bug."
                    },
                    Cleared = clear
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Delete a specific webhook registration from the real Graph API.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/delete-webhook?id=YOUR_WEBHOOK_ID
        /// </summary>
        [HttpGet("delete-webhook")]
        public async Task<IActionResult> DeleteWebhook([FromQuery] string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return BadRequest(new { Error = "Provide ?id=WEBHOOK_ID. Get IDs from /list-webhooks" });

                using var client = CreateGraphClient();
                var (statusCode, body) = await GraphDelete(client, $"/api/webhooks/{id}");

                return Ok(new
                {
                    Step = "6 — Delete Webhook",
                    DeletedId = id,
                    HttpStatus = statusCode,
                    Response = body
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Delete ALL webhooks registered on this Graph account. Use with caution.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/cleanup-webhooks
        /// </summary>
        [HttpGet("cleanup-webhooks")]
        public async Task<IActionResult> CleanupWebhooks()
        {
            try
            {
                using var client = CreateGraphClient();
                var resp = await client.GetAsync("/api/webhooks");
                var body = await resp.Content.ReadAsStringAsync();
                var webhooks = JsonDocument.Parse(body).RootElement;

                var deleted = new List<object>();
                if (webhooks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var wh in webhooks.EnumerateArray())
                    {
                        if (wh.TryGetProperty("id", out var idProp))
                        {
                            var whId = idProp.GetString();
                            var (sc, _) = await GraphDelete(client, $"/api/webhooks/{whId}");
                            deleted.Add(new { Id = whId, HttpStatus = sc });
                        }
                    }
                }

                _lastRegisteredWebhookId = null;

                return Ok(new
                {
                    Step = "7 — Cleanup All Webhooks",
                    TotalDeleted = deleted.Count,
                    Results = deleted
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 8: Analyze real content from Graph to show the docId status suffix pattern.
        /// Queries content and parses each _id to show extracted status vs what substring matching would do.
        /// Sample usage: https://localhost:5009/util-api/custom-webhook-filter/analyze-docid-status
        /// </summary>
        [HttpGet("analyze-docid-status")]
        public async Task<IActionResult> AnalyzeDocIdStatus(
            [FromQuery] int limit = 20)
        {
            try
            {
                var singleKey = _queryOptions.Value.SingleKey;
                var gateway = _queryOptions.Value.GatewayAddress?.TrimEnd('/') ?? "https://staging.cg.optimizely.com";

                var graphqlQuery = $"{{ Content(limit: {limit}) {{ total items {{ ContentType Status Name }} }} }}";

                using var httpClient = new HttpClient();
                var url = $"{gateway}/content/v2?auth={singleKey}";
                var requestBody = JsonSerializer.Serialize(new { query = graphqlQuery });
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                var parsed = JsonDocument.Parse(responseBody).RootElement;

                var analysis = new List<object>();
                if (parsed.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("Content", out var contentNode) &&
                    contentNode.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                        var status = item.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() : null;
                        var contentTypes = item.TryGetProperty("ContentType", out var ctProp) ? ctProp.Clone() : (JsonElement?)null;
                        // Simulate docId format: {name}_{status} (real docIds use {guid}_{lang}_{status})
                        // In real events, the docId comes from the OpenSearch _id field
                        var simulatedDocId = $"content_{name?.Replace(" ", "-") ?? "unknown"}_en_{status}";
                        var extractedStatus = ExtractStatusFromDocId(simulatedDocId);

                        analysis.Add(new
                        {
                            Name = name,
                            ContentType = contentTypes,
                            SimulatedDocId = simulatedDocId,
                            DocumentStatusField = status,
                            ExtractedStatusFromId = extractedStatus,
                            SubstringContainsPublished = simulatedDocId.Contains("Published", StringComparison.OrdinalIgnoreCase),
                            SubstringContainsDraft = simulatedDocId.Contains("Draft", StringComparison.OrdinalIgnoreCase),
                            WouldMatchPublishedFilter_Buggy = simulatedDocId.Contains("Published", StringComparison.OrdinalIgnoreCase),
                            WouldMatchPublishedFilter_Fixed = "Published".Equals(status, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }

                var publishedBySubstring = analysis.Cast<dynamic>().Count(a => (bool)a.WouldMatchPublishedFilter_Buggy);
                var publishedByStatus = analysis.Cast<dynamic>().Count(a => (bool)a.WouldMatchPublishedFilter_Fixed);

                return Ok(new
                {
                    Step = "8 — Analyze Real DocId Status Patterns",
                    TotalDocuments = analysis.Count,
                    MatchPublishedFilter_BuggySubstring = publishedBySubstring,
                    MatchPublishedFilter_FixedExplicit = publishedByStatus,
                    Discrepancy = publishedBySubstring != publishedByStatus,
                    Documents = analysis,
                    Explanation = publishedBySubstring != publishedByStatus
                        ? $"DISCREPANCY: {publishedBySubstring} docs match by substring vs {publishedByStatus} by explicit status. The difference ({publishedBySubstring - publishedByStatus}) would cause incorrect webhook fires."
                        : "No discrepancy found in current snapshot. Edit a published page as draft to create the mismatch."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Utility

        private static string ExtractStatusFromDocId(string docId)
        {
            if (string.IsNullOrEmpty(docId)) return null;
            var knownStatuses = new[] { "Published", "Draft", "PreviouslyPublished" };
            var segments = docId.Split('_');
            for (var i = segments.Length - 1; i >= 0; i--)
            {
                foreach (var status in knownStatuses)
                {
                    if (segments[i].Equals(status, StringComparison.OrdinalIgnoreCase))
                        return status;
                }
            }
            return null;
        }

        #endregion
    }

    #region Models

    public class WebhookReceivedEvent
    {
        public DateTime ReceivedAt { get; set; }
        public string Subject { get; set; }
        public string Action { get; set; }
        public string DocId { get; set; }
        public string ExtractedStatus { get; set; }
        public bool IsBulk { get; set; }
        public int ItemCount { get; set; }
        public JsonElement RawPayload { get; set; }
    }

    #endregion
}
