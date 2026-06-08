namespace Foundation.Custom.Episerver_util_api.ContentGraph.TestBlocks
{
    /// <summary>
    /// Test block with explicit [Searchable(true)] and NO [GraphProperty] attribute.
    /// Expected behavior: Heading and Body SHOULD appear in _fulltext.
    /// </summary>
    [ContentType(
        DisplayName = "[Test] Fulltext - Searchable True",
        GUID = "A1B2C3D4-0004-4000-8000-000000000004",
        Description = "Test block: [Searchable(true)] explicitly set, no [GraphProperty]. Properties should appear in _fulltext.",
        GroupName = "Test - Graph Fulltext")]
    public class FulltextTestBlockSearchableTrue : BlockData
    {
        [CultureSpecific]
        [Searchable(true)]
        [Display(Name = "Heading", Order = 10)]
        public virtual string Heading { get; set; }

        [CultureSpecific]
        [Searchable(true)]
        [Display(Name = "Body", Order = 20)]
        public virtual XhtmlString Body { get; set; }
    }
}
