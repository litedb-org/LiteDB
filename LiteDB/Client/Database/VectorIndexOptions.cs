using System;

namespace LiteDB
{
    /// <summary>
    /// Options used when creating a vector-aware index.
    /// </summary>
    public sealed class VectorIndexOptions
    {
        /// <summary>
        /// Gets the expected dimensionality of the indexed vectors.
        /// </summary>
        public ushort Dimensions { get; }

        /// <summary>
        /// Gets the distance metric used when comparing vectors.
        /// </summary>
        public VectorDistanceMetric Metric { get; }

        public VectorIndexOptions(ushort dimensions, VectorDistanceMetric metric = VectorDistanceMetric.Cosine)
        {
            if (dimensions == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be greater than zero.");
            }

            this.Dimensions = dimensions;
            this.Metric = metric;
        }
    }
}
