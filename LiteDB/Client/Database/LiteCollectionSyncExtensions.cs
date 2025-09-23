using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// Temporary synchronous shims that bridge existing call sites to the new asynchronous-first collection contract.
    /// </summary>
    public static class LiteCollectionSyncExtensions
    {
        [Obsolete("Use UpsertAsync and await the result instead of blocking.")]
        public static bool Upsert<T>(this ILiteCollection<T> collection, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpsertAsync(entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpsertAsync and await the result instead of blocking.")]
        public static int Upsert<T>(this ILiteCollection<T> collection, IEnumerable<T> entities)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpsertAsync(entities).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpsertAsync and await the result instead of blocking.")]
        public static bool Upsert<T>(this ILiteCollection<T> collection, BsonValue id, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpsertAsync(id, entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateAsync and await the result instead of blocking.")]
        public static bool Update<T>(this ILiteCollection<T> collection, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpdateAsync(entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateAsync and await the result instead of blocking.")]
        public static bool Update<T>(this ILiteCollection<T> collection, BsonValue id, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpdateAsync(id, entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateAsync and await the result instead of blocking.")]
        public static int Update<T>(this ILiteCollection<T> collection, IEnumerable<T> entities)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpdateAsync(entities).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateManyAsync and await the result instead of blocking.")]
        public static int UpdateMany<T>(this ILiteCollection<T> collection, BsonExpression transform, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpdateManyAsync(transform, predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateManyAsync and await the result instead of blocking.")]
        public static int UpdateMany<T>(this ILiteCollection<T> collection, Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.UpdateManyAsync(extend, predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use InsertAsync and await the result instead of blocking.")]
        public static BsonValue Insert<T>(this ILiteCollection<T> collection, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.InsertAsync(entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use InsertAsync and await the result instead of blocking.")]
        public static void Insert<T>(this ILiteCollection<T> collection, BsonValue id, T entity)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            collection.InsertAsync(id, entity).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use InsertAsync and await the result instead of blocking.")]
        public static int Insert<T>(this ILiteCollection<T> collection, IEnumerable<T> entities)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.InsertAsync(entities).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use InsertBulkAsync and await the result instead of blocking.")]
        public static int InsertBulk<T>(this ILiteCollection<T> collection, IEnumerable<T> entities, int batchSize = 5000)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.InsertBulkAsync(entities, batchSize).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use EnsureIndexAsync and await the result instead of blocking.")]
        public static bool EnsureIndex<T>(this ILiteCollection<T> collection, string name, BsonExpression expression, bool unique = false)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.EnsureIndexAsync(name, expression, unique).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use EnsureIndexAsync and await the result instead of blocking.")]
        public static bool EnsureIndex<T>(this ILiteCollection<T> collection, BsonExpression expression, bool unique = false)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.EnsureIndexAsync(expression, unique).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use EnsureIndexAsync and await the result instead of blocking.")]
        public static bool EnsureIndex<T, K>(this ILiteCollection<T> collection, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.EnsureIndexAsync(keySelector, unique).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use EnsureIndexAsync and await the result instead of blocking.")]
        public static bool EnsureIndex<T, K>(this ILiteCollection<T> collection, string name, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.EnsureIndexAsync(name, keySelector, unique).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DropIndexAsync and await the result instead of blocking.")]
        public static bool DropIndex<T>(this ILiteCollection<T> collection, string name)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DropIndexAsync(name).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindAsync and await the result instead of blocking.")]
        public static IEnumerable<T> Find<T>(this ILiteCollection<T> collection, BsonExpression predicate, int skip = 0, int limit = int.MaxValue)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(predicate, skip, limit);
            }

            return Materialize(collection.FindAsync(predicate, skip, limit));
        }

        [Obsolete("Use FindAsync and await the result instead of blocking.")]
        public static IEnumerable<T> Find<T>(this ILiteCollection<T> collection, Query query, int skip = 0, int limit = int.MaxValue)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(query, skip, limit);
            }

            return Materialize(collection.FindAsync(query, skip, limit));
        }

        [Obsolete("Use FindAsync and await the result instead of blocking.")]
        public static IEnumerable<T> Find<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(predicate, skip, limit);
            }

            return Materialize(collection.FindAsync(predicate, skip, limit));
        }

        [Obsolete("Use FindByIdAsync and await the result instead of blocking.")]
        public static T FindById<T>(this ILiteCollection<T> collection, BsonValue id)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.FindByIdAsync(id).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindOneAsync and await the result instead of blocking.")]
        public static T FindOne<T>(this ILiteCollection<T> collection, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(predicate).FirstOrDefault();
            }

            return collection.FindOneAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindOneAsync and await the result instead of blocking.")]
        public static T FindOne<T>(this ILiteCollection<T> collection, string predicate, BsonDocument parameters)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(BsonExpression.Create(predicate, parameters)).FirstOrDefault();
            }

            return collection.FindOneAsync(predicate, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindOneAsync and await the result instead of blocking.")]
        public static T FindOne<T>(this ILiteCollection<T> collection, BsonExpression predicate, params BsonValue[] args)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(BsonExpression.Create(predicate, args)).FirstOrDefault();
            }

            return collection.FindOneAsync(predicate, default, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindOneAsync and await the result instead of blocking.")]
        public static T FindOne<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(predicate).FirstOrDefault();
            }

            return collection.FindOneAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindOneAsync and await the result instead of blocking.")]
        public static T FindOne<T>(this ILiteCollection<T> collection, Query query)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindSync(query).FirstOrDefault();
            }

            return collection.FindOneAsync(query).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FindAllAsync and await the result instead of blocking.")]
        public static IEnumerable<T> FindAll<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection.FindAllSync();
            }

            return Materialize(collection.FindAllAsync());
        }

        [Obsolete("Use DeleteAsync and await the result instead of blocking.")]
        public static bool Delete<T>(this ILiteCollection<T> collection, BsonValue id)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteAsync(id).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteAllAsync and await the result instead of blocking.")]
        public static int DeleteAll<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteManyAsync and await the result instead of blocking.")]
        public static int DeleteMany<T>(this ILiteCollection<T> collection, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteManyAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteManyAsync and await the result instead of blocking.")]
        public static int DeleteMany<T>(this ILiteCollection<T> collection, string predicate, BsonDocument parameters)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteManyAsync(predicate, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteManyAsync and await the result instead of blocking.")]
        public static int DeleteMany<T>(this ILiteCollection<T> collection, string predicate, params BsonValue[] args)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteManyAsync(predicate, default, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteManyAsync and await the result instead of blocking.")]
        public static int DeleteMany<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.DeleteManyAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection, string predicate, BsonDocument parameters)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync(predicate, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection, string predicate, params BsonValue[] args)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync(predicate, default, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteCollection<T> collection, Query query)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.CountAsync(query).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection, string predicate, BsonDocument parameters)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync(predicate, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection, string predicate, params BsonValue[] args)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync(predicate, default, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteCollection<T> collection, Query query)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.LongCountAsync(query).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteCollection<T> collection, BsonExpression predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.ExistsAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteCollection<T> collection, string predicate, BsonDocument parameters)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.ExistsAsync(predicate, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteCollection<T> collection, string predicate, params BsonValue[] args)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.ExistsAsync(predicate, default, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteCollection<T> collection, Expression<Func<T, bool>> predicate)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.ExistsAsync(predicate).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteCollection<T> collection, Query query)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.ExistsAsync(query).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MinAsync and await the result instead of blocking.")]
        public static BsonValue Min<T>(this ILiteCollection<T> collection, BsonExpression keySelector)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MinAsync(keySelector).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MinAsync and await the result instead of blocking.")]
        public static BsonValue Min<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MinAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MinAsync and await the result instead of blocking.")]
        public static K Min<T, K>(this ILiteCollection<T> collection, Expression<Func<T, K>> keySelector)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MinAsync(keySelector).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MaxAsync and await the result instead of blocking.")]
        public static BsonValue Max<T>(this ILiteCollection<T> collection, BsonExpression keySelector)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MaxAsync(keySelector).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MaxAsync and await the result instead of blocking.")]
        public static BsonValue Max<T>(this ILiteCollection<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MaxAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use MaxAsync and await the result instead of blocking.")]
        public static K Max<T, K>(this ILiteCollection<T> collection, Expression<Func<T, K>> keySelector)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            return collection.MaxAsync(keySelector).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static IEnumerable<TItem> Materialize<TItem>(IAsyncEnumerable<TItem> source)
        {
            var list = new List<TItem>();
            var enumerator = source.GetAsyncEnumerator(CancellationToken.None);

            try
            {
                while (enumerator.MoveNextAsync().ConfigureAwait(false).GetAwaiter().GetResult())
                {
                    list.Add(enumerator.Current);
                }
            }
            finally
            {
                enumerator.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }

            return list;
        }
    }
}
