using Optimizely.ContentGraph.Cms.NetCore.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks
{
    /// <summary>
    /// Test block with [GraphProperty(PropertyIndexingMode.Default)] on all text properties.
    /// Expected behavior: Heading and Body will be stored and filterable, but NOT searchable / NOT in _fulltext.
    /// Per PropertyUtil.cs line 70-72:
    ///   if (indexingMode is PropertyIndexingMode.OutputOnly or PropertyIndexingMode.Default)
    ///   { return false; }
    /// </summary>
    [ContentType(
        DisplayName = "[Test] Fulltext - Default Mode",
        GUID = "A1B2C3D4-0002-4000-8000-000000000002",
        Description = "Test block: All text properties are Default mode. They should be filterable but NOT appear in _fulltext.",
        GroupName = "Test - Graph Fulltext")]
    public class FulltextTestBlockDefault : BlockData
    {
        [CultureSpecific]
        [Display(Name = "Heading", Order = 10)]
        [GraphProperty(PropertyIndexingMode.Default)]
        public virtual string Heading { get; set; }

        [CultureSpecific]
        [Display(Name = "Body", Order = 20)]
        [GraphProperty(PropertyIndexingMode.Default)]
        public virtual XhtmlString Body { get; set; }
    }
}
