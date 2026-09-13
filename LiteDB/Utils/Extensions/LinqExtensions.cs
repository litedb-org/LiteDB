using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class LinqExtensions
    {
        public static IEnumerable<IEnumerable<T>> Batch<T>(this IEnumerable<T> source, int batchSize)
        {
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    yield return YieldBatchElements(enumerator, batchSize - 1);
                }
            }
        }

        private static IEnumerable<T> YieldBatchElements<T>(IEnumerator<T> source, int batchSize)
        {
            yield return source.Current;

            for (int i = 0; i < batchSize && source.MoveNext(); i++)
            {
                yield return source.Current;
            }
        }

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            return _();

            IEnumerable<TSource> _()
            {
                var knownKeys = new HashSet<TKey>(comparer);

                foreach (var element in source)
                {
                    if (knownKeys.Add(keySelector(element)))
                    {
                        yield return element;
                    }
                }
            }
        }

        /// <summary>
        /// Return same IEnumerable but indicate if item last item in enumerable
        /// </summary>
        public static IEnumerable<LastItem<T>> IsLast<T>(this IEnumerable<T> source) where T : class
        {
            T last = null;

            foreach(var item in source)
            {
                if (last != default(T))
                {
                    yield return new LastItem<T> { Item = last, IsLast = false };
                }

                last = item;
            }

            if (last != null)
            {
                yield return new LastItem<T> { Item = last, IsLast = true };
            }
        }
    }

    internal struct LastItem<T>
    {
        public T Item { get; set; }
        public bool IsLast { get; set; }
    }
}