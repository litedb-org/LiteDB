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
        private DatafileLookup _lookup;
        private PageAddress _scoreAddress = PageAddress.Empty;
        private double _score;
        private bool _hasScore;

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
            _scoreAddress = PageAddress.Empty;
            var results = _limit.HasValue
                ? new VectorIndexService(_snapshot, _collation).Search(_metadata, _target, _maxDistance, _limit,
                    documentLookup: _lookup)
                : this.Scan(indexer);

            foreach (var result in results)
            {
                if (result.Document.RawId.IsEmpty) continue;
                _scoreAddress = result.Document.RawId;
                _score = result.Distance;
                _hasScore = true;
                yield return new IndexNode(result.Document);
            }
        }

        internal bool RequiresSort => !_limit.HasValue;

        internal void ConfigureLookup(DataService data, bool utcDate, HashSet<string> fields)
        {
            var required = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
            if (required.Count > 0) required.UnionWith(_index.BsonExpr.Fields);
            _lookup = new DatafileLookup(data, utcDate, required);
        }

        internal OrderByItem CreateOrderByItem(int order)
        {
            var expression = BsonExpression.Create($"VECTOR_SIM({_index.Expression}, @0)", new BsonVector(_target));
            var direction = _metadata.Metric == VectorDistanceMetric.DotProduct ? -1 : 1;
            return new OrderByItem(expression, order * direction, document => this.GetScore(document.RawId));
        }

        private IEnumerable<(BsonDocument Document, double Distance)> Scan(IndexService indexer)
        {
            foreach (var node in indexer.FindAll(_snapshot.CollectionPage.PK, Query.Ascending))
            {
                var document = this.Load(node.DataBlock);
                _snapshot.Safepoint();
                if (!_hasScore) continue;
                var matches = _metadata.Metric == VectorDistanceMetric.DotProduct
                    ? _maxDistance == double.MaxValue || double.IsPositiveInfinity(_maxDistance) || _score >= _maxDistance
                    : _score <= _maxDistance;
                if (matches) yield return (document, _score);
            }
        }

        public BsonDocument Load(IndexNode node)
        {
            return node.Key as BsonDocument;
        }

        public BsonDocument Load(PageAddress rawId)
        {
            var document = _lookup.Load(rawId);
            var value = _index.BsonExpr.ExecuteScalar(document, _collation);
            _scoreAddress = rawId;
            _hasScore = false;
            if (VectorIndexService.TryExtractVector(value, _metadata.Dimensions, out var vector))
            {
                var distance = VectorIndexService.ComputeDistance(vector, _target, _metadata.Metric, out var similarity);
                _score = _metadata.Metric == VectorDistanceMetric.DotProduct ? similarity : distance;
                _hasScore = !double.IsNaN(_score);
            }
            return document;
        }

        internal bool Matches(VectorScoreProjection projection)
        {
            return VectorExpressionIdentity.HasSameSource(Expression, projection.Field) &&
                _target.SequenceEqual(projection.Target);
        }

        internal bool TryGetScore(PageAddress rawId, out double score)
        {
            // Sort/group pipelines reload by address; retain only the current row's score.
            if (_scoreAddress != rawId) this.Load(rawId);
            score = _score;
            return _hasScore;
        }

        internal BsonValue GetScore(PageAddress rawId) => this.TryGetScore(rawId, out var score) ? score : BsonValue.Null;

        internal LiteDB.Vector.VectorDistanceMetric Metric => _metadata.Metric;

        public override string ToString()
        {
            return "VECTOR INDEX SEARCH";
        }
    }
}
