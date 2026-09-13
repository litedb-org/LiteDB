using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Execute an "index scan" passing a Func as where
    /// </summary>
    internal class IndexScan : Index
    {
        private readonly Func<BsonValue, bool> _func;

        public IndexScan(string name, Func<BsonValue, bool> func, int order)
            : base(name, order)
        {
            _func = func;
        }

        public override uint GetCost(CollectionIndex index)
        {
            return 80;
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            return indexer
                .FindAll(index, this.Order)
                .Where(i => _func(i.Key));
        }

        public override string ToString()
        {
            return string.Format("FULL INDEX SCAN({0})", this.Name);
        }
    }
}