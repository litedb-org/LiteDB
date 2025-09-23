using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    /// <summary>
    /// Temporary synchronous shims that bridge the asynchronous queryable surface with legacy callers.
    /// </summary>
    public static class LiteQueryableSyncExtensions
    {
        [Obsolete("Use ExecuteReaderAsync and await the result instead of blocking.")]
        public static IBsonDataReader ExecuteReader<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ToDocumentsAsync and await the result instead of blocking.")]
        public static IEnumerable<BsonDocument> ToDocuments<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            if (queryable is LiteQueryable<T> liteQueryable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return liteQueryable.EnumerateDocuments();
            }

            return Materialize(queryable.ToDocumentsAsync(cancellationToken));
        }

        [Obsolete("Use ToAsyncEnumerable and await the result instead of blocking.")]
        public static IEnumerable<T> ToEnumerable<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            if (queryable is LiteQueryable<T> liteQueryable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return liteQueryable.Enumerate();
            }

            return Materialize(queryable.ToAsyncEnumerable(cancellationToken));
        }

        [Obsolete("Use ToListAsync and await the result instead of blocking.")]
        public static List<T> ToList<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.ToListAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ToArrayAsync and await the result instead of blocking.")]
        public static T[] ToArray<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.ToArrayAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use GetPlanAsync and await the result instead of blocking.")]
        public static BsonDocument GetPlan<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.GetPlanAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FirstAsync and await the result instead of blocking.")]
        public static T First<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.FirstAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use FirstOrDefaultAsync and await the result instead of blocking.")]
        public static T FirstOrDefault<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use SingleAsync and await the result instead of blocking.")]
        public static T Single<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.SingleAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use SingleOrDefaultAsync and await the result instead of blocking.")]
        public static T SingleOrDefault<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CountAsync and await the result instead of blocking.")]
        public static int Count<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.CountAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use LongCountAsync and await the result instead of blocking.")]
        public static long LongCount<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.LongCountAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExistsAsync and await the result instead of blocking.")]
        public static bool Exists<T>(this ILiteQueryableResult<T> queryable, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.ExistsAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use IntoAsync and await the result instead of blocking.")]
        public static int Into<T>(this ILiteQueryableResult<T> queryable, string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId, CancellationToken cancellationToken = default)
        {
            if (queryable == null) throw new ArgumentNullException(nameof(queryable));

            return queryable.IntoAsync(newCollection, autoId, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static IEnumerable<TItem> Materialize<TItem>(IAsyncEnumerable<TItem> source)
        {
            return MaterializeAsync(source).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static async Task<List<TItem>> MaterializeAsync<TItem>(IAsyncEnumerable<TItem> source)
        {
            var list = new List<TItem>();

            await foreach (var item in source)
            {
                list.Add(item);
            }

            return list;
        }
    }
}
