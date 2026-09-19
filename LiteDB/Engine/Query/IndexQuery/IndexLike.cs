using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly bool _usePrefixSeek;
        private readonly bool _useRangeSeek;
        private readonly BsonValue _upper;
        private readonly StringComparison _comparison;

        public IndexLike(string name, BsonValue value, int order, Collation collation)
            : base(name, order)
        {
            _pattern = value.AsString;
            _startsWith = _pattern.SqlLikeStartsWith(out _testSqlLike);
            _equals = _pattern == _startsWith;
            var isOrdinal = collation.SortOptions == CompareOptions.Ordinal || collation.SortOptions == CompareOptions.OrdinalIgnoreCase;
            // Linguistic sort order can interleave nonmatching prefixes; an early range exit loses matches.
            _usePrefixSeek = _startsWith.Length > 0 && isOrdinal;
            _useRangeSeek = !isOrdinal && LikePrefixRange.TryGetUpperBound(_startsWith, collation, out _upper);
            _comparison = collation.SortOptions == CompareOptions.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            // Preserve the matcher's UTF-16 character semantics even for supplementary case pairs.
            _testSqlLike |= collation.SortOptions == CompareOptions.OrdinalIgnoreCase;
        }

        public override uint GetCost(CollectionIndex index)
        {
            if (_usePrefixSeek || _useRangeSeek) return 10; // similar to equals non-unique

            return 100; // index full scan
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // if contains startsWith string, search using index Find
            // otherwise, use index full scan and test results
            return _usePrefixSeek ? this.ExecuteStartsWith(indexer, index) :
                _useRangeSeek ? this.ExecuteRange(indexer, index) :
                this.ExecuteLike(indexer, index);
        }

        /// <summary>
        /// Walk the keys in [_startsWith, _upper) in index order; the range only narrows, SqlLike still decides.
        /// </summary>
        private IEnumerable<IndexNode> ExecuteRange(IndexService indexer, CollectionIndex index)
        {
            var lower = new BsonValue(_startsWith);
            var node = this.FindRangeStart(indexer, index, this.Order == Query.Ascending ? lower : _upper);

            while (node != null && !node.Key.IsMinValue && !node.Key.IsMaxValue)
            {
                // read before yielding: a safepoint can release the page backing this node
                var next = node.GetNextPrev(0, this.Order);
                var isBelow = node.Key.CompareTo(lower, indexer.Collation) < 0;
                var isAbove = node.Key.CompareTo(_upper, indexer.Collation) >= 0;

                if (this.Order == Query.Ascending ? isAbove : isBelow) break;

                if (!isBelow && !isAbove && node.Key.IsString && node.DataBlock.IsEmpty == false &&
                    node.Key.AsString.SqlLike(_pattern, indexer.Collation))
                {
                    yield return node;
                }

                indexer.Safepoint();
                node = indexer.GetNode(next);
            }
        }

        /// <summary>
        /// Find can land anywhere inside a run of equal keys: rewind to the run's first node in scan direction.
        /// </summary>
        private IndexNode FindRangeStart(IndexService indexer, CollectionIndex index, BsonValue start)
        {
            var node = indexer.Find(index, start, true, this.Order);

            while (node != null)
            {
                var previous = indexer.GetNode(node.GetNextPrev(0, -this.Order));

                if (previous == null || previous.Key.IsMinValue || previous.Key.IsMaxValue ||
                    previous.Key.CompareTo(start, indexer.Collation) != 0) break;

                node = previous;
            }

            return node;
        }

        private IEnumerable<IndexNode> ExecuteStartsWith(IndexService indexer, CollectionIndex index)
        {
            // find first indexNode
            var first = indexer.Find(index, _startsWith, true, this.Order);
            var node = first;

            // if collection exists but are empty
            if (first == null) yield break;

            // A safepoint can release every page backing an IndexNode. Keep only
            // the address needed to begin the forward scan before yielding.
            var forward = first.GetNextPrev(0, this.Order);
            first = null;

            // first, go backward to get all same values
            while (node != null)
            {
                // if current node are edges exit while
                if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                var next = node.GetNextPrev(0, -this.Order);

                if (!node.Key.IsString) break;
                var valueString = node.Key.AsString;

                if (_equals ?
                    valueString.Equals(_startsWith, _comparison) :
                    valueString.StartsWith(_startsWith, _comparison))
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
            node = indexer.GetNode(forward);

            while (node != null)
            {
                // if current node are edges exit while
                if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                var next = node.GetNextPrev(0, this.Order);

                if (!node.Key.IsString) break;
                var valueString = node.Key.AsString;

                if (_equals ?
                    valueString.Equals(_pattern, _comparison) :
                    valueString.StartsWith(_startsWith, _comparison))
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
                _usePrefixSeek || _useRangeSeek ? "INDEX SEEK (+RANGE SCAN)" : "FULL INDEX SCAN",
                this.Name,
                _pattern);
        }
    }
}
