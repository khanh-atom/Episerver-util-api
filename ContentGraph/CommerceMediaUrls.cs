using Optimizely.ContentGraph.Cms.Core.ContentApiModelProperties;
using Optimizely.Graph.Commerce.CommerceContent;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    [ServiceConfiguration(typeof(IContentApiModelProperty), Lifecycle = ServiceInstanceScope.Singleton)]
    public class CommerceMediaUrls : CommerceAssetApiModelBase<IEnumerable<CommerceMediaModel>>
    {
        public CommerceMediaUrls(
            ContentTypeModelRepository repo,
            IContentLoader contentLoader,
            IUrlResolver urlResolver)
            : base(repo, contentLoader, urlResolver) { }

        public override string Name => "AssetRelations";

        public override IEnumerable<CommerceMediaModel> NoValue
            => Enumerable.Empty<CommerceMediaModel>();

        protected override IEnumerable<CommerceMediaModel> GetAssets(
            IEnumerable<CommerceMedia> commerceMediaItems)
        {
            return commerceMediaItems
                .OrderBy(x => x.SortOrder)
                .Select(media => new CommerceMediaModel
                {
                    GroupName = media.GroupName,
                    Url = GetUrl(media),
                    SortOrder = media.SortOrder,
                    AssetType = media.AssetType
                });
        }
    }

    public class CommerceMediaModel
    {
        public string GroupName { get; set; }
        public string Url { get; set; }
        public int SortOrder { get; set; }
        public string AssetType { get; set; }
    }
}
