namespace LiteDB.Vector
{
    /// <summary>
    /// Supported metrics for vector similarity operations.
    /// </summary>
    public enum VectorDistanceMetric : byte
    {
        Euclidean = 0,
        Cosine = 1,
        DotProduct = 2
    }
}
