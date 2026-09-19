using System;
using System.Linq.Expressions;

namespace LiteDB.Vector
{
    /// <summary>
    /// Extension methods that expose vector-aware index creation for <see cref="ILiteCollection{T}"/>.
    /// </summary>
    public static class LiteCollectionVectorExtensions
    {
        public static bool EnsureIndex<T>(this ILiteCollection<T> collection, string name, BsonExpression expression, VectorIndexOptions options)
        {
            return Unwrap(collection).EnsureVectorIndex(name, expression, options);
        }

        public static bool EnsureIndex<T>(this ILiteCollection<T> collection, BsonExpression expression, VectorIndexOptions options)
        {
            return Unwrap(collection).EnsureVectorIndex(expression, options);
        }

        public static bool EnsureIndex<T, K>(this ILiteCollection<T> collection, Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            return Unwrap(collection).EnsureVectorIndex(keySelector, options);
        }

        public static bool EnsureIndex<T, K>(this ILiteCollection<T> collection, string name, Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            return Unwrap(collection).EnsureVectorIndex(name, keySelector, options);
        }

        private static LiteCollection<T> Unwrap<T>(ILiteCollection<T> collection)
        {
            if (collection is null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            if (collection is LiteCollection<T> concrete)
            {
                return concrete;
            }

            throw new ArgumentException("Vector index operations require LiteDB's default collection implementation.", nameof(collection));
        }
    }
}
