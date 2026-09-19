using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement range operation - in asc or desc way - can be used as LT, LTE, GT, GTE too because support MinValue/MaxValue
    /// </summary>
    internal class IndexRange : Index
    {
        private readonly BsonValue _start;
        private readonly BsonValue _end;

        private readonly bool _startEquals;
        private readonly bool _endEquals;

        public IndexRange(string name, BsonValue start, BsonValue end, bool startEquals, bool endEquals, int order)
            : base(name, order)
        {
            _start = start;
            _end = end;

            _startEquals = startEquals;
            _endEquals = endEquals;
        }

        internal bool HasCloserStart(IndexRange other, int order, Collation collation)
        {
            var start = order == Query.Ascending ? _start : _end;
            var previous = order == Query.Ascending ? other._start : other._end;
            var comparison = start.CompareTo(previous, collation);
            if (comparison != 0) return order == Query.Ascending ? comparison > 0 : comparison < 0;
            var inclusive = order == Query.Ascending ? _startEquals : _endEquals;
            var previousInclusive = order == Query.Ascending ? other._startEquals : other._endEquals;
            return !inclusive && previousInclusive;
        }

        /// <summary>
        /// Both ranges constrain the same scalar key, so the tighter start and the tighter end enforce them all
        /// </summary>
        internal IndexRange Intersect(IndexRange other, Collation collation)
        {
            var lower = this.HasCloserStart(other, Query.Ascending, collation) ? this : other;
            var upper = this.HasCloserStart(other, Query.Descending, collation) ? this : other;

            return new IndexRange(this.Name, lower._start, upper._end, lower._startEquals, upper._endEquals, this.Order);
        }

        public override uint GetCost(CollectionIndex index)
        {
            return 20;
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // if order are desc, swap start/end values
            var start = this.Order == Query.Ascending ? _start : _end;
            var end = this.Order == Query.Ascending ? _end : _start;

            var startEquals = this.Order == Query.Ascending ? _startEquals : _endEquals;
            var endEquals = this.Order == Query.Ascending ? _endEquals : _startEquals;

            // the start loop below yields keys equal to start without looking at end
            var bounds = _start.CompareTo(_end, indexer.Collation);

            if (bounds > 0 || (bounds == 0 && !(_startEquals && _endEquals))) yield break;

            // find first indexNode (or get from head/tail if Min/Max value)
            var first = 
                start.Type == BsonType.MinValue ? indexer.GetNode(index.Head) :
                start.Type == BsonType.MaxValue ? indexer.GetNode(index.Tail) :
                indexer.Find(index, start, true, this.Order);

            var node = first;

            // if startsEquals, return all equals value from start linked list
            if (startEquals && node != null)
            {
                // going backward in same value list to get first value
                while (!node.GetNextPrev(0, -this.Order).IsEmpty && ((node = indexer.GetNode(node.GetNextPrev(0, -this.Order))).Key.CompareTo(start, indexer.Collation) == 0))
                {
                    if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                    yield return node;
                }

                node = first;
            }

            // returns (or not) equals start value
            while (node != null)
            {
                var diff = node.Key.CompareTo(start, indexer.Collation);

                // if current value are not equals start, go out this loop
                if (diff != 0) break;

                if (startEquals && !(node.Key.IsMinValue || node.Key.IsMaxValue))
                {
                    yield return node;
                }

                node = indexer.GetNode(node.GetNextPrev(0, this.Order));
            }

            // navigate using next[0] do next node - if less or equals returns
            while (node != null)
            {
                var diff = node.Key.CompareTo(end, indexer.Collation);

                if (endEquals && diff == 0 && !(node.Key.IsMinValue || node.Key.IsMaxValue))
                {
                    yield return node;
                }
                else if (diff == -this.Order && !(node.Key.IsMinValue || node.Key.IsMaxValue))
                {
                    yield return node;
                }
                else
                {
                    break;
                }

                node = indexer.GetNode(node.GetNextPrev(0, this.Order));
            }
        }

        public override string ToString()
        {
            if (_start.IsMinValue && _endEquals == false)
            {
                return string.Format("INDEX SCAN({0} < {1})", this.Name, _end);
            }
            else if (_start.IsMinValue && _endEquals == true)
            {
                return string.Format("INDEX SCAN({0} <= {1})", this.Name, _end);
            }
            else if (_end.IsMaxValue && _startEquals == false)
            {
                return string.Format("INDEX SCAN({0} > {1})", this.Name, _start);
            }
            else if (_end.IsMaxValue && _startEquals == true)
            {
                return string.Format("INDEX SCAN({0} >= {1})", this.Name, _start);
            }
            else if (_startEquals && _endEquals)
            {
                return string.Format("INDEX RANGE SCAN({0} BETWEEN {1} AND {2})", this.Name, _start, _end);
            }
            else
            {
                return string.Format("INDEX RANGE SCAN({0} {1} {2} AND {0} {3} {4})",
                    this.Name, _startEquals ? ">=" : ">", _start, _endEquals ? "<=" : "<", _end);
            }
        }
    }
}