using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Mediachase.Commerce;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Markets;
using Mediachase.Commerce.Pricing;
using EPiServer;
using EPiServer.Core;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.Linking;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Routing;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.CatalogGroup
{
    /// <summary>
    /// API controller to troubleshoot why LowestPriceOfVariationPerMarket is empty in Content Graph.
    /// The field is populated from the LowestPrice DB table, which requires:
    ///   1. "Enable Lowest Price Display" checked per market
    ///   2. A price change event AFTER enabling (to populate LowestPrice table)
    ///   3. Running the "Optimizely Graph Full Synchronization" job
    /// Base route: util-api/custom-lowest-price-graph
    /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph
    /// </summary>
    [ApiController]
    [Route("util-api/custom-lowest-price-graph")]
    public class CustomLowestPriceGraphController : ControllerBase
    {
        private readonly IMarketService _marketService;
        private readonly ILowestPriceService _lowestPriceService;
        private readonly IPriceDetailService _priceDetailService;
        private readonly IContentRepository _contentRepository;
        private readonly ReferenceConverter _referenceConverter;
        private readonly IRequestHostResolver _requestHostResolver;
        private readonly ISiteDefinitionResolver _siteDefinitionResolver;
        private readonly IRelationRepository _relationRepository;

        public CustomLowestPriceGraphController(
            IMarketService marketService,
            ILowestPriceService lowestPriceService,
            IPriceDetailService priceDetailService,
            IContentRepository contentRepository,
            ReferenceConverter referenceConverter,
            IRequestHostResolver requestHostResolver,
            ISiteDefinitionResolver siteDefinitionResolver,
            IRelationRepository relationRepository)
        {
            _marketService = marketService;
            _lowestPriceService = lowestPriceService;
            _priceDetailService = priceDetailService;
            _contentRepository = contentRepository;
            _referenceConverter = referenceConverter;
            _requestHostResolver = requestHostResolver;
            _siteDefinitionResolver = siteDefinitionResolver;
            _relationRepository = relationRepository;
        }

        /// <summary>
        /// Step 0: Overview — shows diagnostic summary and all available endpoints.
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                var markets = _marketService.GetAllMarkets().ToList();
                var lowestPriceMarkets = markets
                    .Where(m => m is ILowestPriceMarket lpm && lpm.IsLowestPriceEnabled)
                    .Select(m => m.MarketId.Value)
                    .ToList();

                var baseUrl = $"{Request.Scheme}://{Request.Host}/util-api/custom-lowest-price-graph";

                return Ok(new
                {
                    Description = "Troubleshoot LowestPriceOfVariationPerMarket empty in Content Graph",
                    Diagnosis = new
                    {
                        TotalMarkets = markets.Count,
                        MarketsWithLowestPriceEnabled = lowestPriceMarkets,
                        EnabledCount = lowestPriceMarkets.Count,
                        AllEnabled = lowestPriceMarkets.Count == markets.Count(m => m.IsEnabled),
                    },
                    Endpoints = new
                    {
                        Step0_Overview = $"{baseUrl}",
                        Step1_CheckMarkets = $"{baseUrl}/step1-check-markets",
                        Step2_CheckPriceDetail = $"{baseUrl}/step2-check-price-detail?productCode=98294",
                        Step3_CheckLowestPriceTable = $"{baseUrl}/step3-check-lowest-price-table?variantCode=170482",
                        Step4_SeedLowestPrice = $"{baseUrl}/step4-seed-lowest-price?variantCode=170482",
                        Step5_ResaveVariantPrice = $"{baseUrl}/step5-resave-variant-price?variantCode=170482",
                        Step6_FullPipeline = $"{baseUrl}/step6-full-pipeline?productCode=98294",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Check all market configurations — verifies IsLowestPriceEnabled per market.
        /// The "Enable Lowest Price Display" checkbox must be checked in Settings > Markets.
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step1-check-markets
        /// </summary>
        [HttpGet("step1-check-markets")]
        public IActionResult Step1CheckMarkets()
        {
            try
            {
                var markets = _marketService.GetAllMarkets().ToList();

                var marketDetails = markets.Select(m =>
                {
                    var isLowestPriceMarket = m is ILowestPriceMarket;
                    var isLowestPriceEnabled = m is ILowestPriceMarket lpm && lpm.IsLowestPriceEnabled;

                    return new
                    {
                        MarketId = m.MarketId.Value,
                        MarketName = m.MarketName,
                        IsEnabled = m.IsEnabled,
                        DefaultCurrency = m.DefaultCurrency.CurrencyCode,
                        DefaultLanguage = m.DefaultLanguage.Name,
                        ImplementsILowestPriceMarket = isLowestPriceMarket,
                        IsLowestPriceEnabled = isLowestPriceEnabled,
                        Status = !m.IsEnabled ? "DISABLED_MARKET"
                            : !isLowestPriceMarket ? "MISSING_INTERFACE"
                            : !isLowestPriceEnabled ? "LOWEST_PRICE_NOT_ENABLED"
                            : "OK"
                    };
                }).ToList();

                var enabledCount = marketDetails.Count(m => m.IsLowestPriceEnabled);

                return Ok(new
                {
                    Step = "1. Check Market Configuration",
                    Summary = enabledCount == 0
                        ? "PROBLEM: No markets have 'Enable Lowest Price Display' enabled. Go to Settings > Markets and check the checkbox."
                        : enabledCount < marketDetails.Count(m => m.IsEnabled)
                            ? $"WARNING: Only {enabledCount}/{marketDetails.Count(m => m.IsEnabled)} enabled markets have Lowest Price enabled."
                            : $"OK: All {enabledCount} enabled markets have Lowest Price enabled.",
                    Markets = marketDetails
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Check PriceDetail entries for all variants of a product.
        /// This verifies that variant prices exist in the database (source data for LowestPrice).
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step2-check-price-detail?productCode=98294
        /// </summary>
        [HttpGet("step2-check-price-detail")]
        public IActionResult Step2CheckPriceDetail(string productCode)
        {
            try
            {
                if (string.IsNullOrEmpty(productCode))
                    return BadRequest("productCode is required");

                var productLink = _referenceConverter.GetContentLink(productCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(productLink))
                    return NotFound(new { Error = $"Product '{productCode}' not found" });

                var product = _contentRepository.Get<EntryContentBase>(productLink);
                var variantLinks = _relationRepository.GetChildren<ProductVariation>(productLink).Select(r => r.Child);
                var variants = _contentRepository
                    .GetItems(variantLinks, product.Language)
                    .OfType<VariationContent>()
                    .ToList();

                var variantPrices = new List<object>();
                foreach (var variant in variants)
                {
                    var prices = _priceDetailService.List(variant.ContentLink).ToList();

                    variantPrices.Add(new
                    {
                        VariantCode = variant.Code,
                        VariantName = variant.Name,
                        PriceCount = prices.Count,
                        Prices = prices.Select(p => new
                        {
                            MarketId = p.MarketId.Value,
                            Currency = p.UnitPrice.Currency.CurrencyCode,
                            Amount = p.UnitPrice.Amount,
                            MinQuantity = p.MinQuantity,
                            ValidFrom = p.ValidFrom,
                            ValidUntil = p.ValidUntil,
                            CustomerPricingType = p.CustomerPricing.PriceTypeId.ToString(),
                            PriceCode = p.CustomerPricing.PriceCode
                        }).ToList()
                    });
                }

                return Ok(new
                {
                    Step = "2. Check PriceDetail for Variants",
                    ProductCode = productCode,
                    ProductName = product.Name,
                    VariantCount = variants.Count,
                    Summary = !variants.Any()
                        ? "PROBLEM: No variants found for this product."
                        : variantPrices.All(vp => ((dynamic)vp).PriceCount == 0)
                            ? "PROBLEM: Variants exist but have no prices configured."
                            : $"OK: {variants.Count} variants with prices found.",
                    Variants = variantPrices
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Check the LowestPrice table entries for a specific variant code.
        /// If this returns empty, the LowestPrice table hasn't been populated (root cause of the issue).
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step3-check-lowest-price-table?variantCode=170482
        /// </summary>
        [HttpGet("step3-check-lowest-price-table")]
        public IActionResult Step3CheckLowestPriceTable(string variantCode)
        {
            try
            {
                if (string.IsNullOrEmpty(variantCode))
                    return BadRequest("variantCode is required");

                var siteId = GetSiteId();
                var lowestPrices = _lowestPriceService.List(new[] { variantCode }, siteId).ToList();

                return Ok(new
                {
                    Step = "3. Check LowestPrice Table",
                    VariantCode = variantCode,
                    SiteId = siteId,
                    EntryCount = lowestPrices.Count,
                    Summary = lowestPrices.Count == 0
                        ? "PROBLEM: LowestPrice table has NO entries for this variant. " +
                          "This is why LowestPriceOfVariationPerMarket is empty in Content Graph. " +
                          "The table is only populated when a price change occurs AFTER enabling 'Lowest Price Display'. " +
                          "Use Step 4 (seed) or Step 5 (re-save price) to populate it."
                        : $"OK: {lowestPrices.Count} entries found in LowestPrice table.",
                    Entries = lowestPrices.Select(lp => new
                    {
                        CatalogEntryCode = lp.CatalogEntryCode,
                        MarketId = lp.MarketId.Value,
                        CurrencyCode = lp.Currency.CurrencyCode,
                        LowestPrice = lp.LowestPrice.Amount,
                        AppliedDate = lp.AppliedDate,
                        SiteId = lp.SiteId
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Seed LowestPrice table using ILowestPriceService.Save() for a variant.
        /// Reads current prices from PriceDetail and writes them into the LowestPrice table.
        /// After this, run the full sync job to populate Content Graph.
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step4-seed-lowest-price?variantCode=170482
        /// </summary>
        [HttpGet("step4-seed-lowest-price")]
        public IActionResult Step4SeedLowestPrice(string variantCode)
        {
            try
            {
                if (string.IsNullOrEmpty(variantCode))
                    return BadRequest("variantCode is required");

                var siteId = GetSiteId();
                var variantLink = _referenceConverter.GetContentLink(variantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { Error = $"Variant '{variantCode}' not found" });

                var prices = _priceDetailService.List(variantLink).ToList();

                if (!prices.Any())
                    return Ok(new { Step = "4. Seed LowestPrice", Error = $"No prices found in PriceDetail for variant '{variantCode}'" });

                // Only pick "All Customers" prices (PriceTypeId == 0)
                var allCustomerPrices = prices
                    .Where(p => p.CustomerPricing.PriceTypeId == CustomerPricing.PriceType.AllCustomers)
                    .ToList();

                if (!allCustomerPrices.Any())
                    return Ok(new { Step = "4. Seed LowestPrice", Error = "No 'All Customers' prices found. LowestPrice only tracks 'All Customers' price type." });

                var lowestPriceValues = allCustomerPrices.Select(p => new LowestPriceValue
                {
                    CatalogEntryCode = variantCode,
                    MarketId = p.MarketId,
                    Currency = p.UnitPrice.Currency,
                    LowestPrice = p.UnitPrice,
                    AppliedDate = DateTime.UtcNow,
                    SiteId = siteId
                }).ToList();

                _lowestPriceService.Save(lowestPriceValues);

                // Verify
                var verification = _lowestPriceService.List(new[] { variantCode }, siteId).ToList();

                return Ok(new
                {
                    Step = "4. Seed LowestPrice Table",
                    VariantCode = variantCode,
                    SiteId = siteId,
                    SeededCount = lowestPriceValues.Count,
                    VerificationCount = verification.Count,
                    Summary = verification.Any()
                        ? $"SUCCESS: Seeded {lowestPriceValues.Count} entries. Now run 'Optimizely Graph Full Synchronization' job to sync to Content Graph."
                        : "WARNING: Seed was called but verification returned empty. Check ILowestPriceService implementation.",
                    SeededEntries = lowestPriceValues.Select(lp => new
                    {
                        CatalogEntryCode = lp.CatalogEntryCode,
                        MarketId = lp.MarketId.Value,
                        CurrencyCode = lp.Currency.CurrencyCode,
                        Price = lp.LowestPrice.Amount,
                        AppliedDate = lp.AppliedDate
                    }).ToList(),
                    NextStep = "Run 'Optimizely Graph Full Synchronization' scheduled job, then query LowestPriceOfVariationPerMarket in Content Graph."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Re-save a variant's prices via IPriceDetailService to trigger the OnPriceUpdated event.
        /// This is the natural way to populate the LowestPrice table — the system tracks the change automatically.
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step5-resave-variant-price?variantCode=170482
        /// </summary>
        [HttpGet("step5-resave-variant-price")]
        public IActionResult Step5ResaveVariantPrice(string variantCode)
        {
            try
            {
                if (string.IsNullOrEmpty(variantCode))
                    return BadRequest("variantCode is required");

                var siteId = GetSiteId();
                var variantLink = _referenceConverter.GetContentLink(variantCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(variantLink))
                    return NotFound(new { Error = $"Variant '{variantCode}' not found" });

                var prices = _priceDetailService.List(variantLink).ToList();

                if (!prices.Any())
                    return Ok(new { Step = "5. Re-save Variant Price", Error = $"No prices found for variant '{variantCode}'" });

                // Before
                var beforeCount = _lowestPriceService.List(new[] { variantCode }, siteId).Count();

                // Re-save all prices (triggers OnPriceUpdated which populates LowestPrice)
                _priceDetailService.Save(prices);

                // After
                var afterEntries = _lowestPriceService.List(new[] { variantCode }, siteId).ToList();

                return Ok(new
                {
                    Step = "5. Re-save Variant Price (triggers LowestPrice tracking)",
                    VariantCode = variantCode,
                    PricesResaved = prices.Count,
                    LowestPriceEntriesBefore = beforeCount,
                    LowestPriceEntriesAfter = afterEntries.Count,
                    Summary = afterEntries.Count > beforeCount
                        ? $"SUCCESS: LowestPrice table now has {afterEntries.Count} entries (was {beforeCount}). Run full sync to update Content Graph."
                        : afterEntries.Count == beforeCount && afterEntries.Any()
                            ? $"OK: LowestPrice table already had {afterEntries.Count} entries. Values may have been updated."
                            : "NOTE: LowestPrice entries unchanged. The OnPriceUpdated event may not have triggered LowestPrice recording. Try Step 4 (seed) instead.",
                    LowestPriceEntries = afterEntries.Select(lp => new
                    {
                        MarketId = lp.MarketId.Value,
                        CurrencyCode = lp.Currency.CurrencyCode,
                        LowestPrice = lp.LowestPrice.Amount,
                        AppliedDate = lp.AppliedDate
                    }).ToList(),
                    NextStep = "Run 'Optimizely Graph Full Synchronization' scheduled job."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Full pipeline check — for a product, checks markets, variant prices, and LowestPrice status.
        /// Gives a complete diagnostic of why LowestPriceOfVariationPerMarket might be empty.
        /// Sample usage: https://localhost:5009/util-api/custom-lowest-price-graph/step6-full-pipeline?productCode=98294
        /// </summary>
        [HttpGet("step6-full-pipeline")]
        public IActionResult Step6FullPipeline(string productCode)
        {
            try
            {
                if (string.IsNullOrEmpty(productCode))
                    return BadRequest("productCode is required");

                var siteId = GetSiteId();

                // 1. Markets
                var markets = _marketService.GetAllMarkets().Where(m => m.IsEnabled).ToList();
                var marketStatus = markets.Select(m => new
                {
                    MarketId = m.MarketId.Value,
                    DefaultCurrency = m.DefaultCurrency.CurrencyCode,
                    IsLowestPriceEnabled = m is ILowestPriceMarket lpm && lpm.IsLowestPriceEnabled
                }).ToList();

                // 2. Product & Variants
                var productLink = _referenceConverter.GetContentLink(productCode, CatalogContentType.CatalogEntry);
                if (ContentReference.IsNullOrEmpty(productLink))
                    return NotFound(new { Error = $"Product '{productCode}' not found" });

                var product = _contentRepository.Get<EntryContentBase>(productLink);
                var variantLinks = _relationRepository.GetChildren<ProductVariation>(productLink).Select(r => r.Child);
                var variants = _contentRepository
                    .GetItems(variantLinks, product.Language)
                    .OfType<VariationContent>()
                    .ToList();

                // 3. Check each variant
                var variantDiagnostics = new List<object>();
                var totalPrices = 0;
                var totalLowestPriceEntries = 0;

                foreach (var variant in variants)
                {
                    var prices = _priceDetailService.List(variant.ContentLink)
                        .Where(p => p.CustomerPricing.PriceTypeId == CustomerPricing.PriceType.AllCustomers)
                        .ToList();
                    var lowestPrices = _lowestPriceService.List(new[] { variant.Code }, siteId).ToList();

                    totalPrices += prices.Count;
                    totalLowestPriceEntries += lowestPrices.Count;

                    variantDiagnostics.Add(new
                    {
                        Code = variant.Code,
                        Name = variant.Name,
                        PriceDetailCount = prices.Count,
                        LowestPriceCount = lowestPrices.Count,
                        PriceDetails = prices.Select(p => new
                        {
                            MarketId = p.MarketId.Value,
                            Currency = p.UnitPrice.Currency.CurrencyCode,
                            Amount = p.UnitPrice.Amount
                        }).ToList(),
                        LowestPriceEntries = lowestPrices.Select(lp => new
                        {
                            MarketId = lp.MarketId.Value,
                            Currency = lp.Currency.CurrencyCode,
                            Amount = lp.LowestPrice.Amount,
                            AppliedDate = lp.AppliedDate
                        }).ToList(),
                        Status = lowestPrices.Any() ? "OK" : prices.Any() ? "MISSING_LOWEST_PRICE" : "NO_PRICES"
                    });
                }

                // Build diagnosis
                var problems = new List<string>();
                if (!marketStatus.Any(m => m.IsLowestPriceEnabled))
                    problems.Add("No markets have 'Enable Lowest Price Display' enabled. Go to Settings > Markets.");
                if (!variants.Any())
                    problems.Add("Product has no variants.");
                if (totalPrices == 0)
                    problems.Add("No 'All Customers' prices found on any variant.");
                if (totalPrices > 0 && totalLowestPriceEntries == 0)
                    problems.Add("Variants have prices but LowestPrice table is EMPTY. Prices were likely set BEFORE enabling the feature. Use step4 or step5 to populate.");

                return Ok(new
                {
                    Step = "6. Full Pipeline Diagnostic",
                    ProductCode = productCode,
                    ProductName = product.Name,
                    SiteId = siteId,
                    VariantCount = variants.Count,
                    TotalPriceDetailEntries = totalPrices,
                    TotalLowestPriceEntries = totalLowestPriceEntries,
                    OverallStatus = !problems.Any() ? "OK — LowestPrice data exists. Run full sync to update Content Graph." : "ISSUES_FOUND",
                    Problems = problems,
                    MarketConfiguration = marketStatus,
                    Variants = variantDiagnostics
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private string GetSiteId()
        {
            var site = _siteDefinitionResolver.GetByHostname(_requestHostResolver.HostName, true, out _);
            return site?.Id.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Implementation of ILowestPriceValue for seeding data via ILowestPriceService.Save()
    /// </summary>
    public class LowestPriceValue : ILowestPriceValue
    {
        public string CatalogEntryCode { get; set; }
        public MarketId MarketId { get; set; }
        public Currency Currency { get; set; }
        public Money LowestPrice { get; set; }
        public DateTime AppliedDate { get; set; }
        public string SiteId { get; set; }
    }
}
