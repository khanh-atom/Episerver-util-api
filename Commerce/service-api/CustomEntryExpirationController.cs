using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EPiServer;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using EPiServer.Security;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using Foundation.Features.CatalogContent.Product;
using Foundation.Features.CatalogContent.Variation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.ServiceApi
{
    /// <summary>
    /// Reproduces the Commerce Service API entry expiration issue by making REAL HTTP calls
    /// to the Service API endpoints (/episerverapi/commerce/entries/).
    /// When IsActive=false + StartDate=EndDate=now is sent via PUT, the Service API's dual-path
    /// processing (publish then legacy deactivate) leaves the entry in an inconsistent state.
    ///
    /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/summary
    /// </summary>
    [ApiController]
    [Route("util-api/custom-entry-expiration")]
    public class CustomEntryExpirationController : ControllerBase
    {
        private readonly IContentRepository _contentRepository;
        private readonly ReferenceConverter _referenceConverter;
        private readonly IContentVersionRepository _contentVersionRepository;
        private readonly IHttpClientFactory _httpClientFactory;

        private const string TestProductCode = "ExpirationTest_Product";
        private const string TestVariantCode = "ExpirationTest_Variant";
        private const string TestCatalogName = "Fashion";

        // Service API OAuth credentials (from Startup.cs OpenIDConnect config)
        private const string OAuthClientId = "postman-client";
        private const string OAuthClientSecret = "postman";

        public CustomEntryExpirationController()
        {
            _contentRepository = ServiceLocator.Current.GetInstance<IContentRepository>();
            _referenceConverter = ServiceLocator.Current.GetInstance<ReferenceConverter>();
            _contentVersionRepository = ServiceLocator.Current.GetInstance<IContentVersionRepository>();
            _httpClientFactory = ServiceLocator.Current.GetInstance<IHttpClientFactory>();
        }

        #region Helper: Get base URL from current request
        private string GetBaseUrl()
        {
            return $"{Request.Scheme}://{Request.Host}";
        }
        #endregion

        #region Helper: Get OAuth token from Service API
        private async Task<string> GetServiceApiTokenAsync()
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            using var client = new HttpClient(handler);
            var baseUrl = GetBaseUrl();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("client_id", OAuthClientId),
                new KeyValuePair<string, string>("client_secret", OAuthClientSecret),
                new KeyValuePair<string, string>("username", "admin@example.com"),
                new KeyValuePair<string, string>("password", "Episerver123!"),
                new KeyValuePair<string, string>("scope", "epi_service_api"),
            });

            var response = await client.PostAsync($"{baseUrl}/api/episerver/connect/token", content);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Token request failed ({response.StatusCode}): {json}");

            var tokenObj = JObject.Parse(json);
            return tokenObj["access_token"]?.ToString()
                ?? throw new Exception($"No access_token in response: {json}");
        }
        #endregion

        #region Helper: Make authenticated Service API call
        private async Task<(int statusCode, string body)> ServiceApiGetAsync(string path, string token)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            var response = await client.GetAsync($"{GetBaseUrl()}{path}");
            var body = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, body);
        }

        private async Task<(int statusCode, string body)> ServiceApiPutAsync(string path, string token, string jsonPayload)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await client.PutAsync($"{GetBaseUrl()}{path}", content);
            var body = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, body);
        }
        #endregion

        #region Helper: Build variant info from content API
        private object BuildVariantInfo(string code)
        {
            var link = _referenceConverter.GetContentLink(code, CatalogContentType.CatalogEntry);
            if (ContentReference.IsNullOrEmpty(link)) return new { error = $"'{code}' not found" };

            var variant = _contentRepository.Get<EntryContentBase>(link);
            return new
            {
                code = variant.Code,
                name = variant.Name,
                contentId = variant.ContentLink.ID,
                versionStatus = variant.Status.ToString(),
                isPendingPublish = variant.IsPendingPublish,
                startPublish = variant.StartPublish?.ToString("O"),
                stopPublish = variant.StopPublish?.ToString("O"),
                startPublishYear = variant.StartPublish?.Year,
                stopPublishYear = variant.StopPublish?.Year,
                isExpired = variant.StopPublish.HasValue && variant.StopPublish.Value < DateTime.UtcNow,
                stopPublishIsNearMinValue = variant.StopPublish.HasValue && variant.StopPublish.Value.Year < 2
            };
        }
        #endregion

        /// <summary>
        /// Step 1: Creates a test product with a variant in a normal published state.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/setup
        /// </summary>
        [HttpGet("setup")]
        public IActionResult Setup()
        {
            try
            {
                var rootLink = _referenceConverter.GetRootLink();
                var catalog = _contentRepository.GetChildren<CatalogContent>(rootLink)
                    .FirstOrDefault(c => c.Name.Equals(TestCatalogName, StringComparison.OrdinalIgnoreCase));
                if (catalog == null)
                    return BadRequest(new { error = $"Catalog '{TestCatalogName}' not found." });

                // Create or get product
                var productLink = _referenceConverter.GetContentLink(TestProductCode, CatalogContentType.CatalogEntry);
                GenericProduct product;
                if (!ContentReference.IsNullOrEmpty(productLink))
                {
                    product = _contentRepository.Get<GenericProduct>(productLink);
                }
                else
                {
                    product = _contentRepository.GetDefault<GenericProduct>(catalog.ContentLink);
                    product.Name = "Expiration Test Product";
                    product.Code = TestProductCode;
                    product.StartPublish = DateTime.UtcNow.AddYears(-1);
                    product.StopPublish = DateTime.UtcNow.AddYears(10);
                    _contentRepository.Save(product, SaveAction.Publish, AccessLevel.NoAccess);
                    productLink = _referenceConverter.GetContentLink(TestProductCode, CatalogContentType.CatalogEntry);
                    product = _contentRepository.Get<GenericProduct>(productLink);
                }

                // Create or reset variant
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (!ContentReference.IsNullOrEmpty(variantLink))
                {
                    // Delete existing (may be in broken state from expire-wrong)
                    _contentRepository.Delete(variantLink, true, AccessLevel.NoAccess);
                }

                {
                    var variant = _contentRepository.GetDefault<GenericVariant>(product.ContentLink);
                    variant.Name = "Expiration Test Variant";
                    variant.Code = TestVariantCode;
                    variant.StartPublish = DateTime.UtcNow.AddYears(-1);
                    variant.StopPublish = DateTime.UtcNow.AddYears(10);
                    _contentRepository.Save(variant, SaveAction.Publish, AccessLevel.NoAccess);
                }

                return Ok(new
                {
                    step = "1-setup",
                    message = "Test product and variant created/reset in published state.",
                    product = new { product.Code, product.Name, contentId = product.ContentLink.ID },
                    variant = BuildVariantInfo(TestVariantCode),
                    nextStep = $"{GetBaseUrl()}/util-api/custom-entry-expiration/inspect"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Inspects the test variant state via both Content API and a real Service API GET call.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/inspect
        /// </summary>
        [HttpGet("inspect")]
        public async Task<IActionResult> Inspect()
        {
            try
            {
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { error = $"Variant '{TestVariantCode}' not found. Run /setup first." });

                // Get token and call real Service API GET
                var token = await GetServiceApiTokenAsync();
                var (statusCode, serviceApiBody) = await ServiceApiGetAsync($"/episerverapi/commerce/entries/{TestVariantCode}/", token);

                object serviceApiParsed = null;
                if (statusCode == 200)
                {
                    var j = JObject.Parse(serviceApiBody);
                    serviceApiParsed = new
                    {
                        isActive = j["IsActive"]?.Value<bool>(),
                        code = j["Code"]?.ToString(),
                        name = j["Name"]?.ToString(),
                        startDate = j["StartDate"]?.ToString(),
                        endDate = j["EndDate"]?.ToString(),
                        publishStatuses = j["PublishStatuses"]?.ToString()
                    };
                }

                return Ok(new
                {
                    step = "2-inspect",
                    contentApiState = BuildVariantInfo(TestVariantCode),
                    serviceApiGet = new
                    {
                        url = $"GET /episerverapi/commerce/entries/{TestVariantCode}/",
                        httpStatus = statusCode,
                        response = serviceApiParsed ?? (object)serviceApiBody
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: REPRODUCES THE BUG — Makes a real Service API PUT call with IsActive=false
        /// and StartDate=EndDate=now, exactly like the customer's payload.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/expire-wrong
        /// </summary>
        [HttpGet("expire-wrong")]
        public async Task<IActionResult> ExpireWrong()
        {
            try
            {
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { error = $"Variant '{TestVariantCode}' not found. Run /setup first." });

                var stateBefore = BuildVariantInfo(TestVariantCode);
                var token = await GetServiceApiTokenAsync();
                var now = DateTime.UtcNow.ToString("O");

                // Build the PUT payload — same pattern as the customer's request
                var payload = new
                {
                    IsActive = false,
                    Code = TestVariantCode,
                    Name = "Expiration Test Variant",
                    StartDate = now,
                    EndDate = now,
                    MetaClass = "GenericVariant",
                    Catalog = TestCatalogName,
                    EntryType = "Variation",
                    InventoryStatus = "Disabled",
                    Prices = Array.Empty<object>(),
                    ChildCatalogEntries = Array.Empty<object>(),
                    WarehouseInventories = Array.Empty<object>(),
                    Associations = Array.Empty<object>(),
                    Assets = Array.Empty<object>(),
                    Nodes = Array.Empty<object>(),
                    Variation = new { MinQuantity = 0, MaxQuantity = 100, Weight = 0, TaxCategory = "" }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);

                // Make the real Service API PUT call
                var (putStatus, putBody) = await ServiceApiPutAsync(
                    $"/episerverapi/commerce/entries/{TestVariantCode}/", token, jsonPayload);

                // Read state after PUT via Content API
                var stateAfterServiceApiPut = BuildVariantInfo(TestVariantCode);

                // Also GET via Service API to see what it returns
                var (getStatus, getBody) = await ServiceApiGetAsync(
                    $"/episerverapi/commerce/entries/{TestVariantCode}/", token);

                object serviceApiGetParsed = null;
                if (getStatus == 200)
                {
                    var j = JObject.Parse(getBody);
                    serviceApiGetParsed = new
                    {
                        isActive = j["IsActive"]?.Value<bool>(),
                        startDate = j["StartDate"]?.ToString(),
                        endDate = j["EndDate"]?.ToString(),
                        publishStatuses = j["PublishStatuses"]?.ToObject<object>()
                    };
                }

                return Ok(new
                {
                    step = "3-expire-wrong (REAL SERVICE API CALL)",
                    description = "Real Service API PUT with IsActive=false, StartDate=EndDate=now — entry demoted from Published to CheckedOut",
                    putRequest = new
                    {
                        url = $"PUT /episerverapi/commerce/entries/{TestVariantCode}/",
                        payload = payload,
                        httpStatus = putStatus,
                        response = string.IsNullOrEmpty(putBody) ? "(empty — 204 No Content)" : putBody
                    },
                    stateBefore,
                    stateAfterPut = stateAfterServiceApiPut,
                    serviceApiGetAfterPut = new
                    {
                        url = $"GET /episerverapi/commerce/entries/{TestVariantCode}/",
                        httpStatus = getStatus,
                        response = serviceApiGetParsed ?? (object)getBody
                    },
                    cmsLink = $"{GetBaseUrl()}/episerver/Commerce/Catalog#context=epi.cms.contentdata:///{variantLink.ID}__CatalogContent&viewsetting=viewlanguage:///en",
                    problems = new[]
                    {
                        "Entry demoted from Published → CheckedOut (Not published yet)",
                        "IsActive = false in Service API (legacy DeactivateEntry path)",
                        "IsPendingPublish = true — contradicts the Publish save action",
                        "StartPublish = StopPublish = now — original dates lost",
                        "Content shows 'This content has expired' but also 'Not published yet'",
                        "NOTE: Customer's older Commerce version keeps entry Published with broken dates (12/31/0) instead"
                    },
                    nextStep = $"{GetBaseUrl()}/util-api/custom-entry-expiration/inspect"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Shows the CORRECT way — makes a real Service API PUT with EndDate in the past, IsActive=true.
        /// Run /setup first to reset state.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/expire-correct
        /// </summary>
        [HttpGet("expire-correct")]
        public async Task<IActionResult> ExpireCorrect()
        {
            try
            {
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { error = $"Variant '{TestVariantCode}' not found. Run /setup first." });

                var stateBefore = BuildVariantInfo(TestVariantCode);
                var token = await GetServiceApiTokenAsync();

                // CORRECT: EndDate in the past, IsActive stays true
                var payload = new
                {
                    IsActive = true,
                    Code = TestVariantCode,
                    Name = "Expiration Test Variant",
                    StartDate = DateTime.UtcNow.AddYears(-1).ToString("O"),
                    EndDate = DateTime.UtcNow.AddSeconds(-10).ToString("O"),
                    MetaClass = "GenericVariant",
                    Catalog = TestCatalogName,
                    EntryType = "Variation",
                    InventoryStatus = "Disabled",
                    Prices = Array.Empty<object>(),
                    ChildCatalogEntries = Array.Empty<object>(),
                    WarehouseInventories = Array.Empty<object>(),
                    Associations = Array.Empty<object>(),
                    Assets = Array.Empty<object>(),
                    Nodes = Array.Empty<object>(),
                    Variation = new { MinQuantity = 0, MaxQuantity = 100, Weight = 0, TaxCategory = "" }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var (putStatus, putBody) = await ServiceApiPutAsync(
                    $"/episerverapi/commerce/entries/{TestVariantCode}/", token, jsonPayload);

                var stateAfter = BuildVariantInfo(TestVariantCode);

                return Ok(new
                {
                    step = "4-expire-correct (REAL SERVICE API CALL)",
                    description = "Made a real PUT with IsActive=true, EndDate in the past",
                    putRequest = new
                    {
                        url = $"PUT /episerverapi/commerce/entries/{TestVariantCode}/",
                        httpStatus = putStatus,
                        response = string.IsNullOrEmpty(putBody) ? "(empty — 204 No Content)" : putBody
                    },
                    stateBefore,
                    stateAfterPut = stateAfter,
                    benefits = new[]
                    {
                        "Entry is cleanly expired — StopPublish is in the past",
                        "IsActive stays true — no hybrid state",
                        "'Manage Expiration' in CMS UI works normally",
                        "Recoverable by setting EndDate to future date"
                    },
                    nextStep = $"{GetBaseUrl()}/util-api/custom-entry-expiration/inspect"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Restores the test variant to a normal published state.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/restore
        /// </summary>
        [HttpGet("restore")]
        public async Task<IActionResult> Restore()
        {
            try
            {
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { error = $"Variant '{TestVariantCode}' not found. Run /setup first." });

                var token = await GetServiceApiTokenAsync();
                var payload = new
                {
                    IsActive = true,
                    Code = TestVariantCode,
                    Name = "Expiration Test Variant",
                    StartDate = DateTime.UtcNow.AddYears(-1).ToString("O"),
                    EndDate = DateTime.UtcNow.AddYears(10).ToString("O"),
                    MetaClass = "GenericVariant",
                    Catalog = TestCatalogName,
                    EntryType = "Variation",
                    InventoryStatus = "Disabled",
                    Prices = Array.Empty<object>(),
                    ChildCatalogEntries = Array.Empty<object>(),
                    WarehouseInventories = Array.Empty<object>(),
                    Associations = Array.Empty<object>(),
                    Assets = Array.Empty<object>(),
                    Nodes = Array.Empty<object>(),
                    Variation = new { MinQuantity = 0, MaxQuantity = 100, Weight = 0, TaxCategory = "" }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var (putStatus, putBody) = await ServiceApiPutAsync(
                    $"/episerverapi/commerce/entries/{TestVariantCode}/", token, jsonPayload);

                var stateAfter = BuildVariantInfo(TestVariantCode);

                return Ok(new
                {
                    step = "5-restore (REAL SERVICE API CALL)",
                    message = "Variant restored via real Service API PUT.",
                    putHttpStatus = putStatus,
                    finalState = stateAfter,
                    nextStep = $"{GetBaseUrl()}/util-api/custom-entry-expiration/inspect"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Deletes the test product and variant.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/cleanup
        /// </summary>
        [HttpGet("cleanup")]
        public IActionResult Cleanup()
        {
            try
            {
                var deleted = new List<string>();
                var variantLink = _referenceConverter.GetContentLink(TestVariantCode, CatalogContentType.CatalogEntry);
                if (!ContentReference.IsNullOrEmpty(variantLink))
                {
                    _contentRepository.Delete(variantLink, true, AccessLevel.NoAccess);
                    deleted.Add(TestVariantCode);
                }
                var productLink = _referenceConverter.GetContentLink(TestProductCode, CatalogContentType.CatalogEntry);
                if (!ContentReference.IsNullOrEmpty(productLink))
                {
                    _contentRepository.Delete(productLink, true, AccessLevel.NoAccess);
                    deleted.Add(TestProductCode);
                }
                return Ok(new { step = "6-cleanup", message = "Test content deleted.", deletedCodes = deleted });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Shows all available steps with sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/summary
        /// </summary>
        [HttpGet("summary")]
        public IActionResult Summary()
        {
            try
            {
                return Ok(new
                {
                    title = "Commerce Entry Expiration — Real Service API Calls",
                    description = "Each step makes REAL HTTP calls to /episerverapi/commerce/entries/ to reproduce the expiration issue.",
                    steps = new[]
                    {
                        "Step 1: GET .../setup — Create test product+variant in published state (Content API)",
                        "Step 2: GET .../inspect — Inspect via Content API + real Service API GET",
                        "Step 3: GET .../expire-wrong — REPRODUCE BUG: real Service API PUT with IsActive=false, StartDate=EndDate=now",
                        "Step 2: GET .../inspect — Re-inspect to see the broken state",
                        "Step 1: GET .../setup — Reset to clean state",
                        "Step 4: GET .../expire-correct — CORRECT: real Service API PUT with EndDate in past, IsActive=true",
                        "Step 5: GET .../restore — Restore via real Service API PUT",
                        "Step 6: GET .../cleanup — Delete test content"
                    },
                    sampleUrls = new
                    {
                        setup = "https://localhost:5009/util-api/custom-entry-expiration/setup",
                        inspect = "https://localhost:5009/util-api/custom-entry-expiration/inspect",
                        expireWrong = "https://localhost:5009/util-api/custom-entry-expiration/expire-wrong",
                        expireCorrect = "https://localhost:5009/util-api/custom-entry-expiration/expire-correct",
                        restore = "https://localhost:5009/util-api/custom-entry-expiration/restore",
                        cleanup = "https://localhost:5009/util-api/custom-entry-expiration/cleanup",
                        summary = "https://localhost:5009/util-api/custom-entry-expiration/summary",
                        inspectAny = "https://localhost:5009/util-api/custom-entry-expiration/inspect-entry?code=SKU-A-1_1"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Inspect any existing entry by code via both Content API and real Service API GET.
        /// Sample usage: https://localhost:5009/util-api/custom-entry-expiration/inspect-entry?code=SKU-A-1_1
        /// </summary>
        [HttpGet("inspect-entry")]
        public async Task<IActionResult> InspectEntry(string code)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                    return BadRequest(new { error = "code query parameter is required." });

                var token = await GetServiceApiTokenAsync();
                var (statusCode, body) = await ServiceApiGetAsync($"/episerverapi/commerce/entries/{code}/", token);

                object serviceApiParsed = null;
                if (statusCode == 200)
                {
                    var j = JObject.Parse(body);
                    serviceApiParsed = new
                    {
                        isActive = j["IsActive"]?.Value<bool>(),
                        code = j["Code"]?.ToString(),
                        name = j["Name"]?.ToString(),
                        startDate = j["StartDate"]?.ToString(),
                        endDate = j["EndDate"]?.ToString(),
                        publishStatuses = j["PublishStatuses"]?.ToObject<object>()
                    };
                }

                return Ok(new
                {
                    contentApiState = BuildVariantInfo(code),
                    serviceApiGet = new
                    {
                        url = $"GET /episerverapi/commerce/entries/{code}/",
                        httpStatus = statusCode,
                        response = serviceApiParsed ?? (object)body
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
