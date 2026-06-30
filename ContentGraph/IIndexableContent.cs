namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// Replicates the customer's shared interface for cross-type filtering in Content Graph.
    /// Content types implementing this interface can be queried across types using
    /// IIndexableContent as the GraphQL root after IncludeInterface registration.
    /// </summary>
    public interface IIndexableContent
    {
        bool HideFromSearchResults { get; set; }
    }
}
