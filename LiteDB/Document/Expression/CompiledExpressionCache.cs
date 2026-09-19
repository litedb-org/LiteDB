using System;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// A fixed-size cache shared by scalar and enumerable expressions. Each
    /// immutable entry is published atomically. Four entries per bucket let
    /// colliding hot expressions coexist without growing the cache. Lookups do not lock; publication is serialized.
    /// </summary>
    internal sealed class CompiledExpressionCache
    {
        private readonly Entry[] _entries;
        private readonly object _publish = new object();
        private const int BucketSize = 4;
        private int _count;
        private int _nextVictim;

        public CompiledExpressionCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
        }

        public int Count => Volatile.Read(ref _count);

        public T Get<T>(string source) where T : class
        {
            var start = this.GetBucketStart(source);
            var end = Math.Min(start + BucketSize, _entries.Length);
            for (var i = start; i < end; i++)
            {
                var entry = Volatile.Read(ref _entries[i]);
                if (entry != null && entry.Source == source && entry.Compiled is T compiled) return compiled;
            }
            return null;
        }

        public T Add<T>(string source, T compiled) where T : class
        {
            if (compiled == null) throw new ArgumentNullException(nameof(compiled));
            lock (_publish)
            {
                var start = this.GetBucketStart(source);
                var end = Math.Min(start + BucketSize, _entries.Length);
                for (var i = start; i < end; i++)
                {
                    var entry = Volatile.Read(ref _entries[i]);
                    if (entry != null && entry.Source == source && entry.Compiled is T existing)
                        return existing;
                    if (entry == null)
                    {
                        Volatile.Write(ref _entries[i], new Entry(source, compiled));
                        Interlocked.Increment(ref _count);
                        return compiled;
                    }
                }
                var victim = start + (int)((uint)++_nextVictim % (uint)(end - start));
                Volatile.Write(ref _entries[victim], new Entry(source, compiled));
                return compiled;
            }
        }

        private int GetBucketStart(string source)
        {
            var buckets = (_entries.Length - 1) / BucketSize + 1;
            return (int)((uint)StringComparer.Ordinal.GetHashCode(source) % (uint)buckets) * BucketSize;
        }

        private sealed class Entry
        {
            public Entry(string source, object compiled)
            {
                this.Source = source;
                this.Compiled = compiled;
            }

            public string Source { get; }
            public object Compiled { get; }
        }
    }
}
