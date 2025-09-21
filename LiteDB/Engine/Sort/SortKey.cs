using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace LiteDB.Engine
{
    internal class SortKey : BsonArray
    {
        private readonly int[] _orders;

        private SortKey(IEnumerable<BsonValue> values, IReadOnlyList<int> orders)
            : base(values?.Select(x => x ?? BsonValue.Null).ToArray() ?? throw new ArgumentNullException(nameof(values)))
        {
            if (orders == null) throw new ArgumentNullException(nameof(orders));

            _orders = orders as int[] ?? orders.ToArray();

            if (_orders.Length != this.Count)
            {
                throw new ArgumentException("Orders length must match values length", nameof(orders));
            }
        }

        public override int CompareTo(BsonValue other)
        {
            return this.CompareTo(other, Collation.Binary);
        }

        public override int CompareTo(BsonValue other, Collation collation)
        {
            if (other is SortKey sortKey)
            {
                var length = Math.Min(this.Count, sortKey.Count);

                for (var i = 0; i < length; i++)
                {
                    var result = this[i].CompareTo(sortKey[i], collation);

                    if (result == 0) continue;

                    return _orders[i] == Query.Descending ? -result : result;
                }

                if (this.Count == sortKey.Count) return 0;

                return this.Count < sortKey.Count ? -1 : 1;
            }

            if (other is BsonArray array)
            {
                return this.CompareTo(new SortKey(array, Enumerable.Repeat(Query.Ascending, array.Count).ToArray()), collation);
            }

            return base.CompareTo(other, collation);
        }

        public static SortKey FromValues(IReadOnlyList<BsonValue> values, IReadOnlyList<int> orders)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (orders == null) throw new ArgumentNullException(nameof(orders));

            return new SortKey(values, orders);
        }

        public static SortKey FromBsonValue(BsonValue value, IReadOnlyList<int> orders)
        {
            if (value is SortKey sortKey) return sortKey;

            if (value is BsonArray array)
            {
                return new SortKey(array.ToArray(), orders);
            }

            return new SortKey(new[] { value }, orders);
        }

        private SortKey(BsonArray array, IReadOnlyList<int> orders)
            : base(array?.ToArray() ?? throw new ArgumentNullException(nameof(array)))
        {
            if (orders == null) throw new ArgumentNullException(nameof(orders));

            _orders = orders as int[] ?? orders.ToArray();

            if (_orders.Length != this.Count)
            {
                throw new ArgumentException("Orders length must match values length", nameof(orders));
            }
        }
    }
}
