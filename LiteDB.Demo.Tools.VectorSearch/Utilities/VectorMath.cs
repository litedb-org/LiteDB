using System;
using System.Collections.Generic;

namespace LiteDB.Demo.Tools.VectorSearch.Utilities
{
    internal static class VectorMath
    {
        public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            if (left == null || right == null)
            {
                return 0d;
            }

            var length = Math.Min(left.Count, right.Count);

            if (length == 0)
            {
                return 0d;
            }

            double dot = 0d;
            double leftMagnitude = 0d;
            double rightMagnitude = 0d;

            for (var i = 0; i < length; i++)
            {
                var l = left[i];
                var r = right[i];

                dot += l * r;
                leftMagnitude += l * l;
                rightMagnitude += r * r;
            }

            if (leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon)
            {
                return 0d;
            }

            return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        }

        public static double CosineDistance(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            var similarity = CosineSimilarity(left, right);
            return 1d - similarity;
        }
    }
}
