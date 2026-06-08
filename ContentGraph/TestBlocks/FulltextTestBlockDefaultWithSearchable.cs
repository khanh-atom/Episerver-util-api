using Optimizely.ContentGraph.Cms.NetCore.Core;

namespace Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks
{
    /// <summary>
    /// Test block combining [GraphProperty(PropertyIndexingMode.Default)] WITH [Searchable(true)].
    /// This tests whether [Searchable(true)] can override the Default mode exclusion from _fulltext.
    /// Per PropertyUtil.cs, Default mode returns false from IsSearchable() BEFORE checking [Searchable].
    /// Expected behavior: Properties should NOT appear in _fulltext (GraphProperty takes precedence).
    /// </summary>
    [ContentType(
        DisplayName = "[Test] Fulltext - Default + Searchable",
        GUID = "A1B2C3D4-0005-4000-8000-000000000005",
        Description = "Test block: Both [GraphProperty(Default)] and [Searchable(true)]. Tests which attribute wins — GraphProperty should take precedence.",
        GroupName = "Test - Graph Fulltext")]
    public class FulltextTestBlockDefaultWithSearchable : BlockData
    {
        [CultureSpecific]
        [Searchable(true)]
        [GraphProperty(PropertyIndexingMode.Default)]
        [Display(Name = "Heading", Order = 10)]
        public virtual string Heading { get; set; }

        [CultureSpecific]
        [Searchable(true)]
        [GraphProperty(PropertyIndexingMode.Default)]
        [Display(Name = "Body", Order = 20)]
        public virtual XhtmlString Body { get; set; }
    }
}
