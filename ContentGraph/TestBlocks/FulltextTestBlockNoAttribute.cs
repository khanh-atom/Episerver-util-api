namespace Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks
{
    /// <summary>
    /// Test block with NO [GraphProperty] attribute on text properties.
    /// The [Searchable] attribute is the CMS-level attribute that controls whether properties appear in _fulltext.
    /// For string/XhtmlString, CMS defaults Searchable=true.
    /// Expected behavior: Heading and Body SHOULD appear in _fulltext (default CMS searchable behavior).
    /// </summary>
    [ContentType(
        DisplayName = "[Test] Fulltext - No Attribute",
        GUID = "A1B2C3D4-0003-4000-8000-000000000003",
        Description = "Test block: No [GraphProperty] attribute. CMS defaults should make string/XhtmlString appear in _fulltext.",
        GroupName = "Test - Graph Fulltext")]
    public class FulltextTestBlockNoAttribute : BlockData
    {
        [CultureSpecific]
        [Display(Name = "Heading", Order = 10)]
        public virtual string Heading { get; set; }

        [CultureSpecific]
        [Display(Name = "Body", Order = 20)]
        public virtual XhtmlString Body { get; set; }
    }
}
