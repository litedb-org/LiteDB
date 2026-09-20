using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal sealed class IndexEmpty : Index
    {
        internal IndexEmpty() : base("_id", Query.Ascending) { }

        public override uint GetCost(CollectionIndex index) => 0;

        public override IEnumerable<IndexNode> Run(CollectionPage collection, IndexService indexer)
        {
            yield break;
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            yield break;
        }

        public override string ToString() => "EMPTY (contradictory predicates)";
    }
}
