using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent an OrderBy definition
    /// </summary>
    internal class OrderBy
    {
        private readonly List<OrderByItem> _segments;

        public OrderBy(IEnumerable<OrderByItem> segments)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));

            _segments = segments.ToList();

            if (_segments.Count == 0)
            {
                throw new ArgumentException("OrderBy requires at least one segment", nameof(segments));
            }
        }

        public IReadOnlyList<OrderByItem> Segments => _segments;

        public BsonExpression PrimaryExpression => _segments[0].Expression;

        public int PrimaryOrder => _segments[0].Order;

        public bool ContainsField(string field) => _segments.Any(x => x.Expression.Fields.Contains(field));
    }

    internal class OrderByItem
    {
        public OrderByItem(BsonExpression expression, int order)
        {
            this.Expression = expression ?? throw new ArgumentNullException(nameof(expression));
            this.Order = order;
        }

        public BsonExpression Expression { get; }

        public int Order { get; }
    }
}
