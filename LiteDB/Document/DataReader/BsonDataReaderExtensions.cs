using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    /// <summary>
    /// Asynchronous helpers for consuming <see cref="IBsonDataReader"/> instances.
    /// </summary>
    public static class BsonDataReaderExtensions
    {
        public static async IAsyncEnumerable<BsonValue> ToAsyncEnumerable(
            this IBsonDataReader reader,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return reader.Current;
                }
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        public static async Task<BsonValue[]> ToArrayAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            var list = await reader.ToListAsync(cancellationToken).ConfigureAwait(false);
            return list.ToArray();
        }

        public static async Task<IList<BsonValue>> ToListAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            var list = new List<BsonValue>();

            await foreach (var item in reader.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                list.Add(item);
            }

            return list;
        }

        public static async Task<BsonValue> FirstAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            await foreach (var item in reader.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }

            throw new InvalidOperationException("Sequence contains no elements");
        }

        public static async Task<BsonValue> FirstOrDefaultAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            await foreach (var item in reader.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }

            return null;
        }

        public static async Task<BsonValue> SingleAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            BsonValue value = null;
            var found = false;

            await foreach (var item in reader.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                {
                    throw new InvalidOperationException("Sequence contains more than one element");
                }

                found = true;
                value = item;
            }

            if (!found)
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            return value!;
        }

        public static async Task<BsonValue> SingleOrDefaultAsync(this IBsonDataReader reader, CancellationToken cancellationToken = default)
        {
            BsonValue value = null;
            var found = false;

            await foreach (var item in reader.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                {
                    throw new InvalidOperationException("Sequence contains more than one element");
                }

                found = true;
                value = item;
            }

            return value;
        }

        [Obsolete("Use ToAsyncEnumerable and await the result instead of blocking.")]
        public static IEnumerable<BsonValue> ToEnumerable(this IBsonDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            try
            {
                while (reader.Read())
                {
                    yield return reader.Current;
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        [Obsolete("Use ToListAsync and await the result instead of blocking.")]
        public static IList<BsonValue> ToList(this IBsonDataReader reader) => ToEnumerable(reader).ToList();

        [Obsolete("Use ToArrayAsync and await the result instead of blocking.")]
        public static BsonValue[] ToArray(this IBsonDataReader reader) => ToEnumerable(reader).ToArray();

        [Obsolete("Use FirstAsync and await the result instead of blocking.")]
        public static BsonValue First(this IBsonDataReader reader) => ToEnumerable(reader).First();

        [Obsolete("Use FirstOrDefaultAsync and await the result instead of blocking.")]
        public static BsonValue FirstOrDefault(this IBsonDataReader reader) => ToEnumerable(reader).FirstOrDefault();

        [Obsolete("Use SingleAsync and await the result instead of blocking.")]
        public static BsonValue Single(this IBsonDataReader reader) => ToEnumerable(reader).Single();

        [Obsolete("Use SingleOrDefaultAsync and await the result instead of blocking.")]
        public static BsonValue SingleOrDefault(this IBsonDataReader reader) => ToEnumerable(reader).SingleOrDefault();
    }
}
