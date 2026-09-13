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
            foreach (var value in _values.Distinct())
            {
                var idx = new IndexEquals(this.Name, value);

                foreach (var node in idx.Execute(indexer, index))
                {
                    yield return node;
                }
            }
        }

        public override string ToString()
        {
            return string.Format("INDEX SEEK({0} IN {1})", this.Name, JsonSerializer.Serialize(_values));
        }
    }
}