using System;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private bool TrySelectVectorIndex(out VectorIndexQuery index, out BsonExpression consumedTerm)
        {
            index = null;
            consumedTerm = null;

            // SQL uses ordinary indexes and the streaming, disk-backed query pipeline.
            if (_query.VectorTarget == null) return false;

            // API arguments choose the index and target. Scalar VECTOR_SIM predicates
            // always mean cosine distance and must remain in the ordinary filter pipeline.
            var expression = CanonicalVectorExpression(_query.VectorField);
            var target = _query.VectorTarget;
            var maxDistance = _query.VectorMaxDistance;
            if (expression == null || target == null) return false;

            if (_terms.Any(term => ReferenceEquals(term, _query.VectorFilter)) &&
                this.TryParseVectorPredicate(_query.VectorFilter, out var filterField, out var filterTarget, out var filterDistance) &&
                VectorExpressionIdentity.HasSameSource(filterField, expression) && target.SequenceEqual(filterTarget))
            {
                consumedTerm = _query.VectorFilter;
                maxDistance = filterDistance;
            }

            foreach (var (candidate, metadata) in _snapshot.CollectionPage.GetVectorIndexes())
            {
                if (!VectorExpressionIdentity.HasSameSource(candidate.Expression, expression))
                {
                    continue;
                }

                if (metadata.Dimensions != target.Length)
                {
                    continue;
                }

                // A strict threshold still needs its predicate after the inclusive index search.
                if (consumedTerm?.Type == BsonExpressionType.LessThan ||
                    consumedTerm?.Type == BsonExpressionType.GreaterThan) consumedTerm = null;

                _vectorPrimaryOrderMatched = _query.GroupBy == null && _query.OrderBy.Count > 0 &&
                    this.TryParseVectorExpression(_query.OrderBy[0].Expression, out var orderField, out var orderTarget) &&
                    VectorExpressionIdentity.HasSameSource(orderField, expression) && target.SequenceEqual(orderTarget);
                _vectorOrderConsumed = _vectorPrimaryOrderMatched && _query.OrderBy.Count == 1 &&
                    _query.OrderBy[0].Order == Query.Ascending;

                // ANN top-k is only valid when no remaining operation can discard or reorder candidates.
                // All other shapes use a complete document scan, including unbounded vector queries.
                int? limit = _query.Limit > 0 && _query.Limit < int.MaxValue && _query.Offset == 0 &&
                    _query.GroupBy == null && _query.Includes.Count == 0 &&
                    (_terms.Count == 0 || (_terms.Count == 1 && _terms[0] == consumedTerm)) &&
                    (_query.OrderBy.Count == 0 || _vectorOrderConsumed) ? _query.Limit : (int?)null;

                index = new VectorIndexQuery(candidate.Name, _snapshot, candidate, metadata, target, maxDistance, limit, _collation);
                _vectorOrderConsumed &= limit.HasValue;

                return true;
            }

            return false;
        }

        private bool TryParseVectorPredicate(BsonExpression predicate, out string expression, out float[] target, out double maxDistance)
        {
            expression = null;
            target = null;
            maxDistance = double.NaN;

            if (predicate == null)
            {
                return false;
            }

            if ((predicate.Type == BsonExpressionType.LessThan || predicate.Type == BsonExpressionType.LessThanOrEqual) &&
                this.TryParseVectorExpression(predicate.Left, out expression, out target) && predicate.Right.IsValue &&
                TryConvertToDouble(predicate.Right?.ExecuteScalar(_collation), out maxDistance))
            {
                return true;
            }

            if ((predicate.Type == BsonExpressionType.GreaterThan || predicate.Type == BsonExpressionType.GreaterThanOrEqual) &&
                this.TryParseVectorExpression(predicate.Right, out expression, out target) && predicate.Left.IsValue &&
                TryConvertToDouble(predicate.Left?.ExecuteScalar(_collation), out maxDistance))
            {
                return true;
            }

            expression = null;
            target = null;
            maxDistance = double.NaN;
            return false;
        }

        private bool TryParseVectorExpression(BsonExpression expression, out string fieldExpression, out float[] target)
        {
            fieldExpression = null;
            target = null;

            if (expression == null || expression.Type != BsonExpressionType.VectorSim)
            {
                return false;
            }

            var field = expression.Left;
            if (field == null || string.IsNullOrEmpty(field.Source) || expression.Right?.IsValue != true)
            {
                return false;
            }

            var targetValue = expression.Right?.ExecuteScalar(_collation);

            if (!TryConvertToVector(targetValue, out target))
            {
                return false;
            }

            fieldExpression = field.Source;
            return true;
        }

        private static bool TryConvertToVector(BsonValue value, out float[] vector)
        {
            vector = null;

            if (value == null || value.IsNull)
            {
                return false;
            }

            if (value.Type == BsonType.Vector)
            {
                vector = value.AsVector.ToArray();
                return true;
            }

            if (!value.IsArray)
            {
                return false;
            }

            var array = value.AsArray;
            var buffer = new float[array.Count];

            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];

                if (item.IsNull)
                {
                    return false;
                }

                try
                {
                    buffer[i] = (float)item.AsDouble;
                }
                catch
                {
                    return false;
                }
            }

            vector = buffer;
            return true;
        }

        private static bool TryConvertToDouble(BsonValue value, out double number)
        {
            number = double.NaN;

            if (value == null || value.IsNull || !value.IsNumber)
            {
                return false;
            }

            number = value.AsDouble;
            return !double.IsNaN(number);
        }

        private static string CanonicalVectorExpression(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                return field;
            }

            field = field.Trim();

            // API overloads already supply expression sources; parsing also accepts bare field names.
            // Prefix only a legacy leading-dot path, never a computed expression such as COALESCE(...).
            try
            {
                return BsonExpression.Create(field.StartsWith(".", StringComparison.Ordinal) ? "$" + field : field).Source;
            }
            catch (LiteException)
            {
                // Hand-built Query instances may contain an invalid index expression.
                // Preserve their ordinary WHERE/ORDER BY execution when no index can match.
                return null;
            }
        }

    }
}
