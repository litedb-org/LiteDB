using System;
using System.Linq.Expressions;

namespace LiteDB.Vector
{
    /// <summary>
    /// Extension methods that expose vector-aware index creation for <see cref="ILiteRepository"/>.
    /// </summary>
    public static class LiteRepositoryVectorExtensions
    {
        public static bool EnsureIndex<T>(this ILiteRepository repository, string name, BsonExpression expression, VectorIndexOptions options, string collectionName = null)
        {
            return Unwrap(repository).EnsureVectorIndex<T>(name, expression, options, collectionName);
        }

        public static bool EnsureIndex<T>(this ILiteRepository repository, BsonExpression expression, VectorIndexOptions options, string collectionName = null)
        {
            return Unwrap(repository).EnsureVectorIndex<T>(expression, options, collectionName);
        }

        public static bool EnsureIndex<T, K>(this ILiteRepository repository, Expression<Func<T, K>> keySelector, VectorIndexOptions options, string collectionName = null)
        {
            return Unwrap(repository).EnsureVectorIndex<T, K>(keySelector, options, collectionName);
        }

        public static bool EnsureIndex<T, K>(this ILiteRepository repository, string name, Expression<Func<T, K>> keySelector, VectorIndexOptions options, string collectionName = null)
        {
            return Unwrap(repository).EnsureVectorIndex<T, K>(name, keySelector, options, collectionName);
        }

        private static LiteRepository Unwrap(ILiteRepository repository)
        {
            if (repository is null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            if (repository is LiteRepository liteRepository)
            {
                return liteRepository;
            }

            throw new ArgumentException("Vector index operations require LiteDB's default repository implementation.", nameof(repository));
        }
    }
}
