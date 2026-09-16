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
                if (intersection == null) intersection = new SortedSet<BsonValue>(values, _collation);
                else intersection.IntersectWith(values);
                if (intersection.Count == 0) return new IndexEmpty();
            }
            var keys = new BsonArray(intersection);
            if (keys.Count == 0) return new IndexEmpty();
            if (keys.Count == 1) return new IndexEquals(name, keys[0]);
            return new IndexIn(name, keys, Query.Ascending);
        }
    }
}
