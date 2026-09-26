using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    // Values are evaluated for this query execution, under its collation. SortedSet
    // uses that comparator for equality too (string/numeric hashes need not agree).
    internal sealed class ScalarIndexConstraint
    {
        private readonly Collation _collation;
        private readonly ScalarBounds _bounds = new ScalarBounds(BsonValue.MinValue, BsonValue.MaxValue, true, true);
        private List<BsonArray> _sets;
        private bool _hasBounds;
        private bool _hasEquality;

        internal ScalarIndexConstraint(Collation collation) { _collation = collation; }

        internal bool Intersect(BsonExpressionType operation, BsonValue value)
        {
            if (operation == BsonExpressionType.In)
            {
                if (_sets == null) _sets = new List<BsonArray>();
                _sets.Add(value.IsArray ? value.AsArray : new BsonArray(value));
                return true;
            }
            else if (operation == BsonExpressionType.Between)
            {
                if (!value.IsArray || value.AsArray.Count != 2) return false;
                _bounds.Intersect(BsonExpressionType.GreaterThanOrEqual, value.AsArray[0], _collation);
                _bounds.Intersect(BsonExpressionType.LessThanOrEqual, value.AsArray[1], _collation);
            }
            else _bounds.Intersect(operation, value, _collation);
            _hasBounds = true;
            _hasEquality |= operation == BsonExpressionType.Equal;
            return true;
        }

        internal Index CreateIndex(string name)
        {
            if (_bounds.IsEmpty(_collation)) return new IndexEmpty();
            if (_sets == null)
            {
                if (_hasEquality) return new IndexEquals(name, _bounds.Lower);
                return new IndexRange(name, _bounds.Lower, _bounds.Upper,
                    _bounds.LowerInclusive, _bounds.UpperInclusive, Query.Ascending);
            }
            var intersection = IntersectSets();
            if (intersection.Count == 0) return new IndexEmpty();
            var keys = new BsonArray(intersection);
            if (keys.Count == 1) return new IndexEquals(name, keys[0]);
            return new IndexIn(name, keys, Query.Ascending);
        }

        internal void AppendRanges(List<ScalarBounds> ranges)
        {
            if (_bounds.IsEmpty(_collation)) return;
            if (_sets == null) ranges.Add(_bounds);
            else
            {
                foreach (var key in IntersectSets()) ranges.Add(new ScalarBounds(key, key, true, true));
            }
        }

        internal List<ScalarBounds> BoundRanges(List<ScalarBounds> context)
        {
            if (_bounds.IsEmpty(_collation)) return new List<ScalarBounds>();
            if (context != null && ScalarIntervals.IsUniversal(context)) context = null;
            if (!_hasBounds && context != null) return context;
            var bounds = new List<ScalarBounds> { _bounds };
            return context == null ? bounds : ScalarIntervals.Intersect(bounds, context, _collation);
        }

        internal List<ScalarBounds> IntersectRanges(List<ScalarBounds> ranges)
        {
            if (_sets == null || ranges.Count == 0) return ranges;
            var result = new List<ScalarBounds>();
            var allowed = ScalarIntervals.IsUniversal(ranges) ? null : ranges;
            var filterFirst = allowed == null || PreferRangeFilter(allowed.Count);
            IEnumerable<BsonValue> keys = IntersectSets(filterFirst ? allowed : null);
            if (!filterFirst) keys = FilterSortedRanges(keys, allowed);
            foreach (var key in keys) result.Add(new ScalarBounds(key, key, true, true));
            return result;
        }

        private bool PreferRangeFilter(int rangeCount)
        {
            // Small Boolean interval sets can discard most input before sorting.
            // Larger contexts come from expanded memberships. Prefer a linear
            // merge there unless a few input keys make binary search cheaper.
            if (rangeCount <= 64) return true;
            long inputs = 0;
            foreach (var set in _sets) inputs += set.Count;
            var levels = 0;
            for (var count = rangeCount; count != 0; count >>= 1) levels++;
            return inputs * levels < inputs + rangeCount;
        }

        private IEnumerable<BsonValue> FilterSortedRanges(IEnumerable<BsonValue> values, List<ScalarBounds> ranges)
        {
            var position = 0;
            foreach (var value in values)
            {
                while (position < ranges.Count)
                {
                    var range = ranges[position];
                    var upper = value.CompareTo(range.Upper, _collation);
                    if (upper > 0 || (upper == 0 && !range.UpperInclusive)) { position++; continue; }
                    var lower = value.CompareTo(range.Lower, _collation);
                    if (lower > 0 || (lower == 0 && range.LowerInclusive)) yield return value;
                    break;
                }
                if (position == ranges.Count) yield break;
            }
        }

        private SortedSet<BsonValue> IntersectSets(List<ScalarBounds> allowed = null)
        {
            // Bounds are complete now. Discard excluded values before constructing
            // ordered sets, and seed from the shortest IN list to keep intersections small.
            var smallest = 0;
            for (var i = 1; i < _sets.Count; i++)
                if (_sets[i].Count < _sets[smallest].Count) smallest = i;
            SortedSet<BsonValue> intersection = null;
            for (var i = 0; i < _sets.Count; i++)
            {
                var set = _sets[i == 0 ? smallest : i == smallest ? 0 : i];
                var values = _hasBounds ? set.Where(x => _bounds.Contains(x, _collation)) : (IEnumerable<BsonValue>)set;
                if (allowed != null) values = FilterRanges(values, allowed);
                if (intersection == null) intersection = new SortedSet<BsonValue>(values, _collation);
                else intersection.IntersectWith(values);
                if (intersection.Count == 0) break;
            }
            return intersection;
        }

        private IEnumerable<BsonValue> FilterRanges(IEnumerable<BsonValue> values, List<ScalarBounds> allowed)
        {
            foreach (var value in values)
                if (ScalarIntervals.Contains(allowed, value, _collation)) yield return value;
        }
    }
}
