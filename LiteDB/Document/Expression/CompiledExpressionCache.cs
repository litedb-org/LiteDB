using System;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// A fixed-size cache shared by scalar and enumerable expressions. Each
    /// immutable entry is published atomically. Four entries per bucket let
    /// colliding hot expressions coexist without growing the cache or locking.
    /// </summary>
    internal sealed class CompiledExpressionCache
    {
        private readonly Entry[] _entries;
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

        public void Add(string source, object compiled)
        {
            if (compiled == null) throw new ArgumentNullException(nameof(compiled));
            var start = this.GetBucketStart(source);
            var end = Math.Min(start + BucketSize, _entries.Length);
            var replacement = new Entry(source, compiled);
            for (var i = start; i < end; i++)
            {
                var entry = Volatile.Read(ref _entries[i]);
                if (entry != null && entry.Source == source && entry.Compiled.GetType() == compiled.GetType()) return;
                if (entry == null)
                {
                    if (Interlocked.CompareExchange(ref _entries[i], replacement, null) == null)
                    {
                        Interlocked.Increment(ref _count);
                        return;
                    }
                    i--; // Inspect the winner before considering the next slot.
                }
            }
            var victim = start + (int)((uint)Interlocked.Increment(ref _nextVictim) % (uint)(end - start));
            Interlocked.Exchange(ref _entries[victim], replacement);
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
