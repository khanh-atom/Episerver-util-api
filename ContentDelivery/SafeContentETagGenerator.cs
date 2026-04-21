using EPiServer.ContentApi.Core.Internal;
using EPiServer.ContentApi.Core.OutputCache.Internal;
using EPiServer.ContentApi.Core.Tracking;

namespace Foundation.Custom.Episerver_util_api.ContentDelivery
{
    public class SafeContentETagGenerator : ContentETagGenerator
    {
        public SafeContentETagGenerator() : base() { }

        public SafeContentETagGenerator(IEnumerable<IContentApiHeaderProvider> contentApiHeaderProviders)
            : base(contentApiHeaderProviders) { }

        /// <inheritdoc/>
        public override string Generate(HttpRequest httpRequestMessage, ContentApiTrackingContext contentApiTrackingContext)
        {
            if (contentApiTrackingContext is null)
            {
                return GenerateFallback(httpRequestMessage);
            }

            try
            {
                return base.Generate(httpRequestMessage, contentApiTrackingContext);
            }
            catch (NullReferenceException ex)
            {

                return GenerateFallback(httpRequestMessage);
            }
        }

        /// <inheritdoc/>
        public override string Generate(IContent content)
        {
            if (content is null)
            {
                return string.Empty;
            }

            if (ContentReference.IsNullOrEmpty(content.ContentLink))
            {
                return string.Empty;
            }

            try
            {
                return base.Generate(content);
            }
            catch (NullReferenceException ex)
            {

                return string.Empty;
            }
        }

        private static string GenerateFallback(HttpRequest httpRequestMessage)
        {
            // Use only the URL hash as a minimal non-cacheable ETag
            var hashCode = new HashCode();
            hashCode.Add(httpRequestMessage?.GetEncodedUrl() ?? string.Empty);
            hashCode.Add(Guid.NewGuid()); // ensure uniqueness to avoid stale caching
            return hashCode.ToHashCode().ToString();
        }
    }
}
