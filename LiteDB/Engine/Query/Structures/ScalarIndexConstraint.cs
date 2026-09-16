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
        private SortedSet<BsonValue> _keys;

        internal ScalarIndexConstraint(Collation collation) { _collation = collation; }

        internal bool Intersect(BsonExpressionType operation, BsonValue value)
        {
            if (operation == BsonExpressionType.In || operation == BsonExpressionType.Equal)
            {
                var keys = operation == BsonExpressionType.In && value.IsArray ?
                    (IEnumerable<BsonValue>)value.AsArray : new[] { value };
                if (_keys == null) _keys = new SortedSet<BsonValue>(keys, _collation);
                else _keys.IntersectWith(keys);
            }
            else if (operation == BsonExpressionType.Between)
            {
                if (!value.IsArray || value.AsArray.Count != 2) return false;
                _bounds.Intersect(BsonExpressionType.GreaterThanOrEqual, value.AsArray[0], _collation);
                _bounds.Intersect(BsonExpressionType.LessThanOrEqual, value.AsArray[1], _collation);
            }
            else _bounds.Intersect(operation, value, _collation);
            return true;
        }

        internal Index CreateIndex(string name)
        {
            if (_bounds.IsEmpty(_collation)) return new IndexEmpty();
            if (_keys == null)
                return new IndexRange(name, _bounds.Lower, _bounds.Upper,
                    _bounds.LowerInclusive, _bounds.UpperInclusive, Query.Ascending);
            var keys = new BsonArray(_keys.Where(x => _bounds.Contains(x, _collation)));
            if (keys.Count == 0) return new IndexEmpty();
            if (keys.Count == 1) return new IndexEquals(name, keys[0]);
            return new IndexIn(name, keys, Query.Ascending);
        }
    }
}
