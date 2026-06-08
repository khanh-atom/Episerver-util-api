using Optimizely.ContentGraph.Cms.NetCore.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks
{
    /// <summary>
    /// Test block with [GraphProperty(PropertyIndexingMode.OutputOnly)] on all text properties.
    /// Expected behavior: Heading and Body will NOT appear in _fulltext.
    /// </summary>
    [ContentType(
        DisplayName = "[Test] Fulltext - OutputOnly",
        GUID = "A1B2C3D4-0001-4000-8000-000000000001",
        Description = "Test block: All text properties are OutputOnly. They should NOT appear in _fulltext.",
        GroupName = "Test - Graph Fulltext")]
    public class FulltextTestBlockOutputOnly : BlockData
    {
        [CultureSpecific]
        [Display(Name = "Heading", Order = 10)]
        [GraphProperty(PropertyIndexingMode.OutputOnly)]
        public virtual string Heading { get; set; }

        [CultureSpecific]
        [Display(Name = "Body", Order = 20)]
        [GraphProperty(PropertyIndexingMode.OutputOnly)]
        public virtual XhtmlString Body { get; set; }
    }
}
