using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Vector
{
    /// <summary>
    /// Extension methods that surface vector-aware query capabilities for <see cref="ILiteQueryable{T}"/>.
    /// </summary>
    public static class LiteQueryableVectorExtensions
    {
        public static ILiteQueryable<T> WhereNear<T>(this ILiteQueryable<T> source, string vectorField, float[] target, double maxDistance)
        {
            return Unwrap(source).VectorWhereNear(vectorField, target, maxDistance);
        }

        public static ILiteQueryable<T> WhereNear<T>(this ILiteQueryable<T> source, BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            return Unwrap(source).VectorWhereNear(fieldExpr, target, maxDistance);
        }

        public static ILiteQueryable<T> WhereNear<T, K>(this ILiteQueryable<T> source, Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            return Unwrap(source).VectorWhereNear(field, target, maxDistance);
        }

        public static IEnumerable<T> FindNearest<T>(this ILiteQueryable<T> source, string vectorField, float[] target, double maxDistance)
        {
            var queryable = Unwrap(source);
            return queryable.VectorWhereNear(vectorField, target, maxDistance).ToEnumerable();
        }

        public static ILiteQueryableResult<T> TopKNear<T, K>(this ILiteQueryable<T> source, Expression<Func<T, K>> field, float[] target, int k)
        {
            return Unwrap(source).VectorTopKNear(field, target, k);
        }

        public static ILiteQueryableResult<T> TopKNear<T>(this ILiteQueryable<T> source, string field, float[] target, int k)
        {
            return Unwrap(source).VectorTopKNear(field, target, k);
        }

        public static ILiteQueryableResult<T> TopKNear<T>(this ILiteQueryable<T> source, BsonExpression fieldExpr, float[] target, int k)
        {
            return Unwrap(source).VectorTopKNear(fieldExpr, target, k);
        }

        private static LiteQueryable<T> Unwrap<T>(ILiteQueryable<T> source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source is LiteQueryable<T> liteQueryable)
            {
                return liteQueryable;
            }

            throw new ArgumentException("Vector operations require LiteDB's default queryable implementation.", nameof(source));
        }
    }
}
