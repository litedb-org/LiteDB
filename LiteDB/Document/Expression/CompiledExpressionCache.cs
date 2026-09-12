using System;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// A fixed-size cache shared by scalar and enumerable expressions. Each
    /// immutable entry is published atomically; collisions replace one entry
    /// without clearing unrelated expressions or coordinating compiler threads.
    /// </summary>
    internal sealed class CompiledExpressionCache
    {
        private readonly Entry[] _entries;
        private int _count;

        public CompiledExpressionCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
        }

        public int Count => Volatile.Read(ref _count);

        public T Get<T>(string source) where T : class
        {
            var entry = Volatile.Read(ref _entries[this.GetSlot(source)]);
            return entry != null && entry.Source == source ? entry.Compiled as T : null;
        }

        public void Add(string source, object compiled)
        {
            var previous = Interlocked.Exchange(ref _entries[this.GetSlot(source)], new Entry(source, compiled));
            if (previous == null) Interlocked.Increment(ref _count);
        }

        private int GetSlot(string source)
        {
            return (int)((uint)StringComparer.Ordinal.GetHashCode(source) % (uint)_entries.Length);
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
