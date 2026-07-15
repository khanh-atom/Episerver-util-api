using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Foundation.Infrastructure.Cms
{
    /// <summary>
    /// Workaround for the Smooth Rebuild stuck at "N/A (undefined)".
    /// 
    /// SmoothRebuildController.GetSyncState() returns a pre-serialized JSON string.
    /// When the MVC pipeline has a JSON output formatter active (common with Content Delivery API,
    /// ConfigureContentDeliveryApiSerializer, or AddControllers().AddJsonOptions()), ASP.NET
    /// double-encodes the string: the raw JSON object becomes a JSON string value wrapped in
    /// outer quotes with escaped inner quotes.
    ///
    /// jQuery (dataType: "json") parses only the outer layer, yielding a string instead of an
    /// object. data.JobPhaseReadable === undefined → the badge shows "N/A (undefined)" and
    /// Accept/Abandon buttons never appear.
    ///
    /// This middleware intercepts the GetSyncState response, detects double-encoding, and
    /// unwraps it so the browser receives a proper JSON object.
    /// 
    /// </summary>
    public class SmoothRebuildGetSyncStateFixMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SmoothRebuildGetSyncStateFixMiddleware> _logger;

        public SmoothRebuildGetSyncStateFixMiddleware(RequestDelegate next, ILogger<SmoothRebuildGetSyncStateFixMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Only intercept GetSyncState requests
            if (!IsGetSyncStateRequest(context.Request))
            {
                await _next(context);
                return;
            }

            // Capture the response body
            var originalBody = context.Response.Body;
            using var memStream = new MemoryStream();
            context.Response.Body = memStream;

            await _next(context);

            memStream.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(memStream).ReadToEndAsync();

            // Detect double-encoded JSON: a JSON string value starts/ends with " and has escaped quotes inside
            // e.g. "{\"JobPhase\":1,\"JobPhaseReadable\":\"SYNCINPROGRESS\",...}"
            if (responseBody.Length > 2 && responseBody[0] == '"' && responseBody[^1] == '"')
            {
                try
                {
                    // Unwrap one layer of JSON string encoding
                    var unwrapped = JsonSerializer.Deserialize<string>(responseBody);
                    if (unwrapped != null)
                    {
                        _logger.LogWarning(
                            "SmoothRebuild GetSyncState response was double-encoded by MVC JSON formatter. " +
                            "Unwrapped to fix Smooth Rebuild UI. ");
                        responseBody = unwrapped;
                    }
                }
                catch (JsonException)
                {
                    // Not actually double-encoded, leave as-is
                }
            }

            // Write the (possibly fixed) response with correct content type
            context.Response.Body = originalBody;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(responseBody);
            await context.Response.WriteAsync(responseBody);
        }

        private static bool IsGetSyncStateRequest(HttpRequest request)
        {
            return request.Method == "GET"
                && request.Path.Value != null
                && request.Path.Value.EndsWith("/SmoothRebuild/GetSyncState", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
