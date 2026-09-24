using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Merge sorted containers with a heap of their next keys.
    /// </summary>
    internal sealed class SortMerge
    {
        private readonly Entry[] _heap;
        private readonly Collation _collation;
        private readonly int _order;
        private int _count;

        internal SortMerge(IReadOnlyList<SortContainer> containers, Collation collation, int order)
        {
            _collation = collation;
            _order = order;
            _heap = new Entry[containers.Count];
            for (var i = 0; i < containers.Count; i++)
            {
                if (!containers[i].IsEOF) _heap[_count++] = new Entry(containers[i], i);
            }
            for (var i = _count / 2 - 1; i >= 0; i--) SiftDown(i);
        }

        internal IEnumerable<KeyValuePair<BsonValue, PageAddress>> Sort()
        {
            if (_count == 0) yield break;
            var current = Pop();
            while (true)
            {
                var previousKey = current.Container.Current.Key;
                yield return current.Container.Current;
                if (!current.Container.MoveNext())
                {
                    if (_count == 0) yield break;
                    current = Pop();
                }
                else if (_count > 0 && current.Container.Current.Key != previousKey && CompareKeys(current, _heap[0]) > 0)
                {
                    var next = _heap[0];
                    _heap[0] = current;
                    SiftDown(0);
                    current = next;
                }
                // Keep the old shortcut for repeated identical keys, and retain
                // the active container for collation ties. Other tied containers
                // use original order, matching the previous merge's tie policy.
            }
        }

        private Entry Pop()
        {
            var result = _heap[0];
            _count--;
            if (_count > 0)
            {
                _heap[0] = _heap[_count];
                _heap[_count] = default;
                SiftDown(0);
            }
            else
            {
                _heap[0] = default;
            }
            return result;
        }

        private void SiftDown(int position)
        {
            var value = _heap[position];
            while (position * 2 + 1 < _count)
            {
                var child = position * 2 + 1;
                if (child + 1 < _count && Compare(_heap[child + 1], _heap[child]) < 0) child++;
                if (Compare(value, _heap[child]) <= 0) break;
                _heap[position] = _heap[child];
                position = child;
            }
            _heap[position] = value;
        }

        private int CompareKeys(Entry left, Entry right) =>
            left.Container.Current.Key.CompareTo(right.Container.Current.Key, _collation) * _order;

        private int Compare(Entry left, Entry right)
        {
            var result = CompareKeys(left, right);
            return result != 0 ? result : left.Ordinal.CompareTo(right.Ordinal);
        }

        private readonly struct Entry
        {
            internal Entry(SortContainer container, int ordinal)
            {
                Container = container;
                Ordinal = ordinal;
            }

            internal SortContainer Container { get; }
            internal int Ordinal { get; }
        }
    }
}
