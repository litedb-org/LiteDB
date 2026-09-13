using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Return all index nodes
    /// </summary>
    internal class IndexAll : Index
    {
        public IndexAll(string name, int order)
            : base(name, order)
        {
        }

        public override uint GetCost(CollectionIndex index)
        {
            return 100; // worst index cost
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            return indexer.FindAll(index, this.Order);
        }

        public override string ToString()
        {
            return string.Format("FULL INDEX SCAN({0})", this.Name);
        }
    }
}