using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement equals index operation =
    /// </summary>
    internal class IndexEquals : Index
    {
        private readonly BsonValue _value;

        public IndexEquals(string name, BsonValue value)
            : base(name, Query.Ascending)
        {
            _value = value;
        }

        public override uint GetCost(CollectionIndex index)
        {
            if (index.Unique) return 1; // best index cost

            return 10; // 
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            var node = indexer.Find(index, _value, false, Query.Ascending);

            if (node == null) yield break;

            yield return node;

            if (index.Unique == false)
            {
                // navigate in both sides to return all nodes found
                var first = node;

                // first go forward
                while (!node.Next[0].IsEmpty && ((node = indexer.GetNode(node.Next[0])).Key.CompareTo(_value, indexer.Collation) == 0))
                {
                    if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                    yield return node;
                }

                node = first;
                
                // and than, go backward
                while (!node.Prev[0].IsEmpty && ((node = indexer.GetNode(node.Prev[0])).Key.CompareTo(_value, indexer.Collation) == 0))
                {
                    if (node.Key.IsMinValue || node.Key.IsMaxValue) break;

                    yield return node;
                }
            }
        }

        public override string ToString()
        {
            return string.Format("INDEX SEEK({0} = {1})", this.Name, _value);
        }
    }
}