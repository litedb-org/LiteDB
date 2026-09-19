using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Vector
{
    public static partial class LiteQueryableVectorExtensions
    {
        /// <summary>
        /// Returns each vector search hit with its score and metric. Call after WhereNear or
        /// TopKNear. Further LINQ operations run over the returned sequence in the application.
        /// </summary>
        /// <remarks>
        /// Indexed searches reuse the index score. Unindexed searches calculate cosine distance
        /// for the returned documents. Grouped and aggregate queries are not supported.
        /// </remarks>
        public static IEnumerable<VectorSearchResult<T>> WithScore<T>(this ILiteQueryableResult<T> source)
        {
            return Unwrap(source).VectorWithScore();
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> WhereNearWithScore<T>(this ILiteQueryable<T> source,
            string vectorField, float[] target, double maxDistance)
        {
            return source.WhereNear(vectorField, target, maxDistance).WithScore();
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> WhereNearWithScore<T>(this ILiteQueryable<T> source,
            BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            return source.WhereNear(fieldExpr, target, maxDistance).WithScore();
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> WhereNearWithScore<T, K>(this ILiteQueryable<T> source,
            Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            return source.WhereNear(field, target, maxDistance).WithScore();
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> FindNearestWithScore<T>(this ILiteQueryable<T> source,
            string vectorField, float[] target, double maxDistance)
        {
            return source.WhereNearWithScore(vectorField, target, maxDistance);
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> FindNearestWithScore<T>(this ILiteQueryable<T> source,
            BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            return source.WhereNearWithScore(fieldExpr, target, maxDistance);
        }

        /// <summary>
        /// Finds documents within the threshold and returns their scores and metrics.
        /// For dot-product indexes, maxDistance is a minimum similarity.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> FindNearestWithScore<T, K>(this ILiteQueryable<T> source,
            Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            return source.WhereNearWithScore(field, target, maxDistance);
        }

        /// <summary>
        /// Returns the nearest k documents with their scores and metrics, best matches first.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> TopKNearWithScore<T>(this ILiteQueryable<T> source,
            string field, float[] target, int k)
        {
            return source.TopKNear(field, target, k).WithScore();
        }

        /// <summary>
        /// Returns the nearest k documents with their scores and metrics, best matches first.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> TopKNearWithScore<T>(this ILiteQueryable<T> source,
            BsonExpression fieldExpr, float[] target, int k)
        {
            return source.TopKNear(fieldExpr, target, k).WithScore();
        }

        /// <summary>
        /// Returns the nearest k documents with their scores and metrics, best matches first.
        /// </summary>
        public static IEnumerable<VectorSearchResult<T>> TopKNearWithScore<T, K>(this ILiteQueryable<T> source,
            Expression<Func<T, K>> field, float[] target, int k)
        {
            return source.TopKNear(field, target, k).WithScore();
        }
    }
}
