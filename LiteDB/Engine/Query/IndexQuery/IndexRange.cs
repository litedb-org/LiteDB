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
                while (!node.GetNextPrev(0, -this.Order).IsEmpty && ((node = indexer.GetNode(node.GetNextPrev(0, -this.Order))).Key.CompareTo(start) == 0))
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
            else
            {
                return string.Format("INDEX RANGE SCAN({0} BETWEEN {1} AND {2})", this.Name, _start, _end);
            }
        }
    }
}