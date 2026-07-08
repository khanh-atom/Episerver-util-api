using EPiServer.Commerce.Marketing;
using EPiServer.Validation;
using Foundation.Infrastructure.Commerce.Marketing;

namespace Foundation.Custom.EpiserverUtilApi.Commerce.Marketing
{
    /// <summary>
    /// Editor-Time Validation for PromotionData.
    ///
    /// Prevents editors from saving/publishing a promotion that has a high-value discount
    /// (≥ 50%) but no coupon code configured. This addresses Zendesk #1913503 where a
    /// 100% discount was unintentionally applied to all orders because the coupon code
    /// field was left blank.
    ///
    /// How it works:
    ///   - Implements IValidate{PromotionData} — the CMS validation hook that fires
    ///     whenever an editor saves or publishes any PromotionData content.
    ///   - Checks both the built-in single Coupon.Code property AND unique/multi-use
    ///     coupons via IUniqueCouponService.
    ///   - If neither is present, the promotion is considered "coupon-less" and the
    ///     validator emits a Warning (configurable to Error via severity constant).
    ///
    /// Limitation: This only applies at save/publish time in the CMS editor UI.
    ///             It does NOT affect runtime cart evaluation. For runtime enforcement,
    ///             see a custom IPromotionProcessor approach.
    ///
    /// Reference: https://docs.developers.optimizely.com/content-management-system/docs/validation
    /// Ticket:    https://optimizely.zendesk.com/agent/tickets/1913503
    /// </summary>
    public class PromotionCouponValidator : IValidate<PromotionData>
    {
        private readonly IUniqueCouponService _uniqueCouponService;

        public PromotionCouponValidator(IUniqueCouponService uniqueCouponService)
        {
            _uniqueCouponService = uniqueCouponService;
        }

        public IEnumerable<ValidationError> Validate(PromotionData promotion)
        {
            // ── Step 1: Determine if the promotion has any coupon codes ──
            var hasSingleCoupon = !string.IsNullOrWhiteSpace(promotion.Coupon?.Code);
            var hasUniqueCoupons = false;

            if (!hasSingleCoupon)
            {
                // Only query the service if there's no single coupon already
                var uniqueCoupons = _uniqueCouponService.GetByPromotionId(promotion.ContentLink.ID);
                hasUniqueCoupons = uniqueCoupons != null && uniqueCoupons.Any();
            }

            var hasCoupon = hasSingleCoupon || hasUniqueCoupons;

            // ── Step 2: If no coupon is configured, emit a validation warning/error ──
            if (!hasCoupon)
            {
                yield return new ValidationError
                {
                    ErrorMessage = "This promotion has no coupon code configured. " +
                                   "Without a coupon code, the promotion will apply automatically " +
                                   "to all qualifying orders. If this is a high-value discount, " +
                                   "consider adding a coupon code to prevent unintended usage.",
                    Severity = ValidationErrorSeverity.Error,
                    ValidationType = ValidationErrorType.StorageValidation
                };
            }
        }
    }
}
