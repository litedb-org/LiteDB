using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Vector
{
    /// <summary>
    /// Extension methods that surface vector-aware query capabilities for <see cref="ILiteQueryable{T}"/>.
    /// </summary>
    public static partial class LiteQueryableVectorExtensions
    {
        /// <summary>Filter using the selected vector index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Use at most one WhereNear per query. A combined TopKNear must use the same expression and target.</remarks>
        /// <exception cref="InvalidOperationException">The query already has WhereNear, or an incompatible TopKNear.</exception>
        public static ILiteQueryable<T> WhereNear<T>(this ILiteQueryable<T> source, string vectorField, float[] target, double maxDistance)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorQuery(inner => inner.VectorWhereNear(vectorField, target, maxDistance));
            return Unwrap(source).VectorWhereNear(vectorField, target, maxDistance);
        }

        /// <summary>Filter using the selected vector index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Use at most one WhereNear per query. A combined TopKNear must use the same expression and target.</remarks>
        /// <exception cref="InvalidOperationException">The query already has WhereNear, or an incompatible TopKNear.</exception>
        public static ILiteQueryable<T> WhereNear<T>(this ILiteQueryable<T> source, BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorQuery(inner => inner.VectorWhereNear(fieldExpr, target, maxDistance));
            return Unwrap(source).VectorWhereNear(fieldExpr, target, maxDistance);
        }

        /// <summary>Filter using the selected vector index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Use at most one WhereNear per query. A combined TopKNear must use the same expression and target.</remarks>
        /// <exception cref="InvalidOperationException">The query already has WhereNear, or an incompatible TopKNear.</exception>
        public static ILiteQueryable<T> WhereNear<T, K>(this ILiteQueryable<T> source, Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorQuery(inner => inner.VectorWhereNear(field, target, maxDistance));
            return Unwrap(source).VectorWhereNear(field, target, maxDistance);
        }

        public static IEnumerable<T> FindNearest<T>(this ILiteQueryable<T> source, string vectorField, float[] target, double maxDistance)
        {
            if (source is TransactionQueryResult<T> bound)
                return bound.VectorEnumerable(inner => inner.VectorWhereNear(vectorField, target, maxDistance).ToEnumerable());
            var queryable = Unwrap(source);
            return queryable.VectorWhereNear(vectorField, target, maxDistance).ToEnumerable();
        }

        /// <summary>Return the nearest k results using the selected index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Simple bounded searches with a matching index use approximate ANN.
        /// When combined with WhereNear, the expression and target must match; its threshold is preserved.</remarks>
        /// <exception cref="InvalidOperationException">An existing WhereNear uses a different expression or target.</exception>
        public static ILiteQueryableResult<T> TopKNear<T, K>(this ILiteQueryable<T> source, Expression<Func<T, K>> field, float[] target, int k)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorResult(inner => inner.VectorTopKNear(field, target, k));
            return Unwrap(source).VectorTopKNear(field, target, k);
        }

        /// <summary>Return the nearest k results using the selected index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Simple bounded searches with a matching index use approximate ANN.
        /// When combined with WhereNear, the expression and target must match; its threshold is preserved.</remarks>
        /// <exception cref="InvalidOperationException">An existing WhereNear uses a different expression or target.</exception>
        public static ILiteQueryableResult<T> TopKNear<T>(this ILiteQueryable<T> source, string field, float[] target, int k)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorResult(inner => inner.VectorTopKNear(field, target, k));
            return Unwrap(source).VectorTopKNear(field, target, k);
        }

        /// <summary>Return the nearest k results using the selected index's metric, or cosine when no vector index matches.</summary>
        /// <remarks>Simple bounded searches with a matching index use approximate ANN.
        /// When combined with WhereNear, the expression and target must match; its threshold is preserved.</remarks>
        /// <exception cref="InvalidOperationException">An existing WhereNear uses a different expression or target.</exception>
        public static ILiteQueryableResult<T> TopKNear<T>(this ILiteQueryable<T> source, BsonExpression fieldExpr, float[] target, int k)
        {
            if (source is TransactionQueryResult<T> bound) return bound.VectorResult(inner => inner.VectorTopKNear(fieldExpr, target, k));
            return Unwrap(source).VectorTopKNear(fieldExpr, target, k);
        }

        private static LiteQueryable<T> Unwrap<T>(ILiteQueryableResult<T> source)
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
