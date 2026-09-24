using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement IN index operation. Value must be an array
    /// </summary>
    internal class IndexIn : Index
    {
        private readonly BsonArray _values;

        internal BsonArray Values => _values;

        public IndexIn(string name, BsonArray values, int order)
            : base(name, order)
        {
            _values = values;
        }

        public override uint GetCost(CollectionIndex index)
        {
            return index.Unique ?
                (uint)_values.Count * 1 :
                (uint)_values.Count * 10;
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // The planner can consume ORDER BY using this index. Seek keys must
            // therefore follow that order, with equality defined by the database collation.
            var values = this.Order == Query.Ascending ?
                _values.OrderBy(x => x, indexer.Collation) : _values.OrderByDescending(x => x, indexer.Collation);
            BsonValue previous = null;
            foreach (var value in values)
            {
                if (previous != null && previous.CompareTo(value, indexer.Collation) == 0) continue;
                previous = value;
                var idx = new IndexEquals(this.Name, value);

                foreach (var node in idx.Execute(indexer, index))
                {
                    yield return node;
                }

                indexer.Safepoint();
            }
        }

        public override string ToString()
        {
            return string.Format("INDEX SEEK({0} IN {1})", this.Name, JsonSerializer.Serialize(_values));
        }
    }
}
