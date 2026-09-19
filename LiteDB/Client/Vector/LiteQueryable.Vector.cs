using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB
{
    public partial class LiteQueryable<T>
    {
        private static void ValidateVectorArguments(float[] target, double maxDistance)
        {
            if (target == null || target.Length == 0) throw new ArgumentException("Target vector must be provided.", nameof(target));
            // Dot-product queries interpret "maxDistance" as a minimum similarity score and may therefore pass negative values.
            if (double.IsNaN(maxDistance)) throw new ArgumentOutOfRangeException(nameof(maxDistance), "Similarity threshold must be a valid number.");
        }

        private static BsonExpression CreateVectorSimilarityFilter(BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            if (fieldExpr == null) throw new ArgumentNullException(nameof(fieldExpr));

            ValidateVectorArguments(target, maxDistance);

            var targetArray = new BsonArray(target.Select(v => new BsonValue(v)));
            return BsonExpression.Create($"{fieldExpr.Source} VECTOR_SIM @0 <= @1", targetArray, new BsonValue(maxDistance));
        }

        internal ILiteQueryable<T> VectorWhereNear(string vectorField, float[] target, double maxDistance)
        {
            if (string.IsNullOrWhiteSpace(vectorField)) throw new ArgumentNullException(nameof(vectorField));

            var fieldExpr = BsonExpression.Create($"$.{vectorField}");
            return this.VectorWhereNear(fieldExpr, target, maxDistance);
        }

        internal ILiteQueryable<T> VectorWhereNear(BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            var filter = CreateVectorSimilarityFilter(fieldExpr, target, maxDistance);

            if (_query.VectorFilter != null)
            {
                throw new InvalidOperationException("Only one WhereNear predicate is supported per query.");
            }
            if (_query.HasVectorFilter) this.ValidateMatchingVectorSearch(fieldExpr, target);

            _query.Where.Add(filter);
            _query.VectorFilter = filter;

            _query.VectorField = fieldExpr.Source;
            _query.VectorTarget = target?.ToArray();
            _query.VectorMaxDistance = maxDistance;

            return this;
        }

        internal ILiteQueryable<T> VectorWhereNear<K>(Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));

            var fieldExpr = _mapper.GetExpression(field);
            return this.VectorWhereNear(fieldExpr, target, maxDistance);
        }

        internal ILiteQueryableResult<T> VectorTopKNear<K>(Expression<Func<T, K>> field, float[] target, int k)
        {
            var fieldExpr = _mapper.GetExpression(field);
            return this.VectorTopKNear(fieldExpr, target, k);
        }

        internal ILiteQueryableResult<T> VectorTopKNear(string field, float[] target, int k)
        {
            var fieldExpr = BsonExpression.Create($"$.{field}");
            return this.VectorTopKNear(fieldExpr, target, k);
        }

        internal ILiteQueryableResult<T> VectorTopKNear(BsonExpression fieldExpr, float[] target, int k)
        {
            if (fieldExpr == null) throw new ArgumentNullException(nameof(fieldExpr));
            if (target == null || target.Length == 0) throw new ArgumentException("Target vector must be provided.", nameof(target));
            if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "Top-K must be greater than zero.");
            if (_query.VectorFilter != null) this.ValidateMatchingVectorSearch(fieldExpr, target);

            var targetArray = new BsonArray(target.Select(v => new BsonValue(v)));

            // Build VECTOR_SIM as order clause
            var simExpr = BsonExpression.Create($"VECTOR_SIM({fieldExpr.Source}, @0)", targetArray);

            _query.VectorField = fieldExpr.Source;
            _query.VectorTarget = target?.ToArray();
            _query.VectorMaxDistance = double.MaxValue;

            return this
                .OrderBy(simExpr, Query.Ascending)
                .Limit(k);
        }

        private void ValidateMatchingVectorSearch(BsonExpression fieldExpr, float[] target)
        {
            if (!VectorExpressionIdentity.HasSameSource(_query.VectorField, fieldExpr.Source) ||
                !_query.VectorTarget.SequenceEqual(target))
            {
                throw new InvalidOperationException("WhereNear and TopKNear must use the same vector expression and target.");
            }
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.WhereNear extension instead.")]
        public ILiteQueryable<T> WhereNear(string vectorField, float[] target, double maxDistance)
        {
            return this.VectorWhereNear(vectorField, target, maxDistance);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.WhereNear extension instead.")]
        public ILiteQueryable<T> WhereNear(BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            return this.VectorWhereNear(fieldExpr, target, maxDistance);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.WhereNear extension instead.")]
        public ILiteQueryable<T> WhereNear<K>(Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            return this.VectorWhereNear(field, target, maxDistance);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.FindNearest extension instead.")]
        public IEnumerable<T> FindNearest(string vectorField, float[] target, double maxDistance)
        {
            return this.VectorWhereNear(vectorField, target, maxDistance).ToEnumerable();
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.TopKNear extension instead.")]
        public ILiteQueryableResult<T> TopKNear<K>(Expression<Func<T, K>> field, float[] target, int k)
        {
            return this.VectorTopKNear(field, target, k);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.TopKNear extension instead.")]
        public ILiteQueryableResult<T> TopKNear(string field, float[] target, int k)
        {
            return this.VectorTopKNear(field, target, k);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call the LiteQueryableVectorExtensions.TopKNear extension instead.")]
        public ILiteQueryableResult<T> TopKNear(BsonExpression fieldExpr, float[] target, int k)
        {
            return this.VectorTopKNear(fieldExpr, target, k);
        }

        internal IEnumerable<VectorSearchResult<T>> VectorWithScore()
        {
            if (!_query.HasVectorFilter)
            {
                throw new InvalidOperationException("WithScore requires WhereNear or TopKNear.");
            }
            if (_query.GroupBy != null || _query.Select.UseSource)
            {
                throw new InvalidOperationException("Vector scores require individual documents, not aggregates or groups.");
            }

            // Snapshot the terminal query so ordinary materialization and subsequent query changes
            // cannot accidentally consume or alter the score envelope.
            var query = new Query
            {
                Select = _query.Select,
                Offset = _query.Offset,
                Limit = _query.Limit,
                ForUpdate = _query.ForUpdate,
                VectorField = _query.VectorField,
                VectorTarget = _query.VectorTarget.ToArray(),
                VectorMaxDistance = _query.VectorMaxDistance,
                VectorFilter = _query.VectorFilter,
                VectorScore = new VectorScoreProjection(_query.VectorField, _query.VectorTarget)
            };
            query.Where.AddRange(_query.Where);
            query.Includes.AddRange(_query.Includes);
            query.OrderBy.AddRange(_query.OrderBy);

            return ReadVectorResults(query);
        }

        private IEnumerable<VectorSearchResult<T>> ReadVectorResults(Query query)
        {
            using (var reader = _engine.Query(_collection, query))
            {
                while (reader.Read())
                {
                    var result = reader.Current.AsDocument;
                    var projected = result["Document"].AsDocument;
                    projected.IsProjectionValue = result.IsProjectionValue;
                    var value = _isSimpleType ? projected[projected.Keys.First()] : projected;
                    var document = _isSimpleType || typeof(T) == typeof(BsonDocument)
                        ? (T)_mapper.Deserialize(typeof(T), value) : _mapper.ToObject<T>(projected);
                    yield return new VectorSearchResult<T>(document,
                        result["Score"].IsNull ? (double?)null : result["Score"].AsDouble,
                        (VectorDistanceMetric)result["Metric"].AsInt32);
                }
            }
        }

    }
}
