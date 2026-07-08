using EPiServer.Commerce.Marketing;
using EPiServer.Core;
using EPiServer.Validation;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.Marketing
{
    /// <summary>
    /// Debug API to inspect the coupon validation state of promotions.
    /// Allows support engineers to quickly identify promotions that have no coupon
    /// codes configured and would apply universally.
    ///
    /// Sample URLs:
    ///   GET https://localhost:5000/util-api/promotion-validation/check-all
    ///   GET https://localhost:5000/util-api/promotion-validation/check/{promotionId}
    /// </summary>
    [ApiController]
    [Route("util-api/promotion-validation")]
    public class PromotionValidationController : ControllerBase
    {
        private readonly IContentLoader _contentLoader;
        private readonly IEnumerable<IValidate<PromotionData>> _validators;

        public PromotionValidationController(
            IContentLoader contentLoader,
            IEnumerable<IValidate<PromotionData>> validators)
        {
            _contentLoader = contentLoader;
            _validators = validators;
        }

        /// <summary>
        /// Runs all registered IValidate{PromotionData} validators against a single promotion.
        /// Sample: https://localhost:5000/util-api/promotion-validation/check/123
        /// </summary>
        [HttpGet("check/{promotionId:int}")]
        public IActionResult CheckPromotion(int promotionId)
        {
            try
            {
                var contentRef = new ContentReference(promotionId);
                if (!_contentLoader.TryGet<PromotionData>(contentRef, out var promotion))
                {
                    return NotFound($"Promotion with ID {promotionId} not found.");
                }

                var results = RunValidation(promotion);
                return Ok(results);
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Scans ALL promotions across all campaigns and reports which ones
        /// would trigger validation warnings/errors (i.e., have no coupon codes).
        /// Sample: https://localhost:5000/util-api/promotion-validation/check-all
        /// </summary>
        [HttpGet("check-all")]
        public IActionResult CheckAllPromotions()
        {
            try
            {
                var campaigns = _contentLoader.GetChildren<SalesCampaign>(SalesCampaignFolder.CampaignRoot);

                var allResults = new List<object>();
                var totalPromotions = 0;
                var totalWithIssues = 0;

                foreach (var campaign in campaigns)
                {
                    var promotions = _contentLoader.GetChildren<PromotionData>(campaign.ContentLink);
                    foreach (var promotion in promotions)
                    {
                        totalPromotions++;
                        var result = RunValidation(promotion);
                        if (result.ValidationErrors.Any())
                        {
                            totalWithIssues++;
                            allResults.Add(result);
                        }
                    }
                }

                return Ok(new
                {
                    TotalPromotions = totalPromotions,
                    TotalWithIssues = totalWithIssues,
                    TotalClean = totalPromotions - totalWithIssues,
                    FlaggedPromotions = allResults
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private PromotionValidationResult RunValidation(PromotionData promotion)
        {
            var errors = new List<ValidationErrorDetail>();

            foreach (var validator in _validators)
            {
                var validationErrors = validator.Validate(promotion);
                errors.AddRange(validationErrors.Select(e => new ValidationErrorDetail
                {
                    ValidatorType = validator.GetType().Name,
                    ErrorMessage = e.ErrorMessage,
                    Severity = e.Severity.ToString(),
                    PropertyName = e.PropertyName
                }));
            }

            return new PromotionValidationResult
            {
                PromotionId = promotion.ContentLink.ID,
                PromotionName = promotion.Name,
                CouponCode = promotion.Coupon?.Code,
                IsActive = promotion.IsActive,
                ValidationErrors = errors
            };
        }

        private class PromotionValidationResult
        {
            public int PromotionId { get; set; }
            public string PromotionName { get; set; }
            public string CouponCode { get; set; }
            public bool IsActive { get; set; }
            public List<ValidationErrorDetail> ValidationErrors { get; set; }
        }

        private class ValidationErrorDetail
        {
            public string ValidatorType { get; set; }
            public string ErrorMessage { get; set; }
            public string Severity { get; set; }
            public string PropertyName { get; set; }
        }
    }
}
