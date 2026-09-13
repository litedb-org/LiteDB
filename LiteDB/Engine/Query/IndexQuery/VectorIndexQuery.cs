using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Vector;

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
        private readonly bool _preserveUndefined;

        private readonly Dictionary<PageAddress, (BsonDocument Document, BsonValue Score)> _cache = new Dictionary<PageAddress, (BsonDocument, BsonValue)>();

        public string Expression => _index.Expression;

        public VectorIndexQuery(
            string name,
            Snapshot snapshot,
            CollectionIndex index,
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit,
            Collation collation,
            bool preserveUndefined)
            : base(name, Query.Ascending)
        {
            _snapshot = snapshot;
            _index = index;
            _metadata = metadata;
            _target = target;
            _maxDistance = maxDistance;
            _limit = limit;
            _collation = collation;
            _preserveUndefined = preserveUndefined;
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
            var results = _limit.HasValue
                ? service.Search(_metadata, _target, _maxDistance, _limit)
                    .Select(x => (x.Document, Distance: new BsonValue(x.Distance)))
                : this.Scan(indexer).OrderBy(x => _metadata.Metric == VectorDistanceMetric.DotProduct && x.Distance.IsNumber
                    ? new BsonValue(-x.Distance.AsDouble) : x.Distance);

            foreach (var result in results)
            {
                var rawId = result.Document.RawId;

                if (rawId.IsEmpty)
                {
                    continue;
                }

                _cache[rawId] = (result.Document, result.Distance);
                yield return new IndexNode(result.Document);
            }
        }

        private IEnumerable<(BsonDocument Document, BsonValue Distance)> Scan(IndexService indexer)
        {
            var data = new DataService(_snapshot, _snapshot.MaxItemsCount);
            var lookup = new DatafileLookup(data, false, null);
            var target = new BsonVector(_target);
            foreach (var node in indexer.FindAll(_snapshot.CollectionPage.PK, Query.Ascending))
            {
                var document = lookup.Load(node);
                var value = _index.BsonExpr.ExecuteScalar(document, _collation);
                _snapshot.Safepoint();
                if (_preserveUndefined)
                {
                    // Use the scalar evaluator for SQL, including null and non-indexable values.
                    yield return (document, BsonExpressionMethods.VECTOR_SIM(value, target));
                    continue;
                }
                if (!VectorIndexService.TryExtractVector(value, _metadata.Dimensions, out var vector)) continue;

                var distance = VectorIndexService.ComputeDistance(vector, _target, _metadata.Metric, out var similarity);
                if (_metadata.Metric == VectorDistanceMetric.DotProduct)
                {
                    if (!double.IsNaN(similarity) && (_maxDistance == double.MaxValue ||
                        double.IsPositiveInfinity(_maxDistance) || similarity >= _maxDistance))
                    {
                        yield return (document, similarity);
                    }
                }
                else if (!double.IsNaN(distance) && distance <= _maxDistance)
                {
                    yield return (document, distance);
                }
            }
        }

        public BsonDocument Load(IndexNode node)
        {
            return node.Key as BsonDocument;
        }

        public BsonDocument Load(PageAddress rawId)
        {
            return _cache.TryGetValue(rawId, out var result) ? result.Document : null;
        }

        internal bool Matches(VectorScoreProjection projection)
        {
            return string.Equals(Expression, projection.Field, StringComparison.OrdinalIgnoreCase) &&
                _target.SequenceEqual(projection.Target);
        }

        internal bool TryGetScore(PageAddress rawId, out double score)
        {
            score = default;
            if (!_cache.TryGetValue(rawId, out var result) || !result.Score.IsNumber)
            {
                return false;
            }

            score = result.Score.AsDouble;
            return true;
        }

        internal BsonValue GetScore(PageAddress rawId) => _cache[rawId].Score;

        internal LiteDB.Vector.VectorDistanceMetric Metric => _metadata.Metric;

        public override string ToString()
        {
            return "VECTOR INDEX SEARCH";
        }
    }
}
