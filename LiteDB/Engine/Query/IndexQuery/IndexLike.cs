using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class IndexLike : Index
    {
        private readonly string _startsWith;
        private readonly bool _equals;
        private readonly bool _testSqlLike;
        private readonly string _pattern;

        public IndexLike(string name, BsonValue value, int order)
            : base(name, order)
        {
            _pattern = value.AsString;
            _startsWith = _pattern.SqlLikeStartsWith(out _testSqlLike);
            _equals = _pattern == _startsWith;
        }

        public override uint GetCost(CollectionIndex index)
        {
            if (_startsWith.Length > 0) return 10; // similar to equals non-unique

            return 100; // index full scan
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // if contains startsWith string, search using index Find
            // otherwise, use index full scan and test results
            return _startsWith.Length > 0 ? 
                this.ExecuteStartsWith(indexer, index) : 
                this.ExecuteLike(indexer, index);
        }

        private IEnumerable<IndexNode> ExecuteStartsWith(IndexService indexer, CollectionIndex index)
        {
            // find first indexNode
            var first = indexer.Find(index, _startsWith, true, this.Order);
            var node = first;

            // if collection exists but are empty
            if (first == null) yield break;

            // first, go backward to get all same values
            while (node != null)
            {
                // if current node are edges exit while
                if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                var next = node.GetNextPrev(0, -this.Order);

                var valueString = 
                    node.Key.IsString ? node.Key.AsString : 
                    node.Key.IsNull ? "" :
                    node.Key.ToString();

                if (_equals ?
                    valueString.Equals(_startsWith, StringComparison.OrdinalIgnoreCase) :
                    valueString.StartsWith(_startsWith, StringComparison.OrdinalIgnoreCase))
                {
                    // must still testing SqlLike method for rest of pattern - only if exists more to test (avoid slow SqlLike test)
                    if ((_testSqlLike == false) ||
                        (_testSqlLike == true && valueString.SqlLike(_pattern, indexer.Collation) == true))
                    {
                        yield return node;
                    }
                }
                else
                {
                    break;
                }

                indexer.Safepoint();
                node = indexer.GetNode(next);
            }

            // move forward
            node = indexer.GetNode(first.GetNextPrev(0, this.Order));

            while (node != null)
            {
                // if current node are edges exit while
                if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                var next = node.GetNextPrev(0, this.Order);

                var valueString =
                    node.Key.IsString ? node.Key.AsString :
                    node.Key.IsNull ? "" :
                    node.Key.ToString();

                if (_equals ?
                    valueString.Equals(_pattern, StringComparison.OrdinalIgnoreCase) :
                    valueString.StartsWith(_startsWith, StringComparison.OrdinalIgnoreCase))
                {
                    // must still testing SqlLike method for rest of pattern - only if exists more to test (avoid slow SqlLike test)
                    if (node.DataBlock.IsEmpty == false &&
                        ((_testSqlLike == false) ||
                        (_testSqlLike == true && valueString.SqlLike(_pattern, indexer.Collation) == true)))
                    {
                        yield return node;
                    }
                }
                else
                {
                    break;
                }

                indexer.Safepoint();
                node = indexer.GetNode(next);
            }
        }

        private IEnumerable<IndexNode> ExecuteLike(IndexService indexer, CollectionIndex index)
        {
            foreach (var node in indexer.FindAll(index, this.Order))
            {
                var matches = node.Key.IsString && node.Key.AsString.SqlLike(_pattern, indexer.Collation);

                if (matches) yield return node;

                indexer.Safepoint();
            }
        }

        public override string ToString()
        {
            return string.Format("{0}({1} LIKE \"{2}\")",
                _startsWith.Length > 0 ? "INDEX SEEK (+RANGE SCAN)" : "FULL INDEX SCAN",
                this.Name,
                _pattern);
        }
    }
}
