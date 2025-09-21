using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexQuery : Index, IDocumentLookup
    {
        private readonly Snapshot _snapshot;
        private readonly CollectionIndex _index;
        private readonly VectorIndexMetadata _metadata;
        private readonly float[] _target;
        private readonly double _maxDistance;
        private readonly int? _limit;
        private readonly Collation _collation;

        private readonly Dictionary<PageAddress, BsonDocument> _cache = new Dictionary<PageAddress, BsonDocument>();

        public string Expression => _index.Expression;

        public VectorIndexQuery(
            string name,
            Snapshot snapshot,
            CollectionIndex index,
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit,
            Collation collation)
            : base(name, Query.Ascending)
        {
            _snapshot = snapshot;
            _index = index;
            _metadata = metadata;
            _target = target;
            _maxDistance = maxDistance;
            _limit = limit;
            _collation = collation;
        }

        public override uint GetCost(CollectionIndex index)
        {
            return 1;
        }

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            throw new NotSupportedException();
        }

        public override IEnumerable<IndexNode> Run(CollectionPage col, IndexService indexer)
        {
            _cache.Clear();

            var service = new VectorIndexService(_snapshot, _collation);
            var results = service.Search(col, indexer, _index, _metadata, _target, _maxDistance, _limit).ToArray();

            foreach (var result in results)
            {
                var rawId = result.Document.RawId;

                if (rawId.IsEmpty)
                {
                    continue;
                }

                _cache[rawId] = result.Document;
                yield return new IndexNode(result.Document);
            }
        }

        public BsonDocument Load(IndexNode node)
        {
            return node.Key as BsonDocument;
        }

        public BsonDocument Load(PageAddress rawId)
        {
            return _cache.TryGetValue(rawId, out var document) ? document : null;
        }

        public override string ToString()
        {
            return "VECTOR INDEX SEARCH";
        }
    }
}
