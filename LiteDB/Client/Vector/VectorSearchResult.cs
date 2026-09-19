namespace LiteDB.Vector
{
    /// <summary>
    /// A vector search hit and the metric value used to rank it.
    /// </summary>
    public sealed class VectorSearchResult<T>
    {
        /// <summary>
        /// Gets the matching document or query projection.
        /// </summary>
        public T Document { get; }

        /// <summary>
        /// Gets the cosine distance (1 - cosine similarity), Euclidean distance, or dot-product
        /// similarity. Smaller distances and larger dot products are better matches.
        /// Null means the unindexed cosine calculation is undefined, for example for a zero vector.
        /// </summary>
        public double? Score { get; }

        /// <summary>
        /// Gets the index metric, or Cosine when the search falls back to VECTOR_SIM.
        /// </summary>
        public VectorDistanceMetric Metric { get; }

        internal VectorSearchResult(T document, double? score, VectorDistanceMetric metric)
        {
            Document = document;
            Score = score;
            Metric = metric;
        }
    }
}
