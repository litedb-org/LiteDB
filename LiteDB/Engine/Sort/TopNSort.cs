using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    // Keep only the best offset + limit keys, bounded by the caller. The maximum
    // heap root is the worst retained candidate. Sequence breaks ties stably.
    internal sealed class TopNSort
    {
        internal const int MaximumCapacity = 1024;
        private readonly Entry[] _heap;
        private readonly Collation _collation;
        private readonly int _order;
        private int _count;
        private long _sequence;

        internal TopNSort(int capacity, Collation collation, int order)
        {
            _heap = new Entry[capacity];
            _collation = collation;
            _order = order;
        }

        internal void Add(BsonValue key, PageAddress address)
        {
            // Match the disk sort's key-size contract even for discarded candidates.
            if (IndexNode.GetKeyLength(key, false) > MAX_INDEX_KEY_LENGTH)
                throw LiteException.InvalidIndexKey($"Sort key must be less than {MAX_INDEX_KEY_LENGTH} bytes.");
            var item = new Entry { Key = key, Address = address, Sequence = _sequence++ };
            if (_count < _heap.Length)
            {
                var index = _count++;
                while (index > 0)
                {
                    var parent = (index - 1) / 2;
                    if (Compare(item, _heap[parent]) <= 0) break;
                    _heap[index] = _heap[parent];
                    index = parent;
                }
                _heap[index] = item;
                return;
            }
            if (Compare(item, _heap[0]) >= 0) return;
            var position = 0;
            while (position * 2 + 1 < _count)
            {
                var child = position * 2 + 1;
                if (child + 1 < _count && Compare(_heap[child + 1], _heap[child]) > 0) child++;
                if (Compare(item, _heap[child]) >= 0) break;
                _heap[position] = _heap[child];
                position = child;
            }
            _heap[position] = item;
        }

        internal IEnumerable<PageAddress> GetAddresses(int offset)
        {
            Array.Sort(_heap, 0, _count, Comparer<Entry>.Create(Compare));
            for (var i = offset; i < _count; i++) yield return _heap[i].Address;
        }

        private int Compare(Entry left, Entry right)
        {
            var comparison = left.Key.CompareTo(right.Key, _collation);
            if (comparison != 0) return _order == Query.Descending ? -comparison : comparison;
            return left.Sequence.CompareTo(right.Sequence);
        }

        private struct Entry
        {
            internal BsonValue Key;
            internal PageAddress Address;
            internal long Sequence;
        }
    }
}
