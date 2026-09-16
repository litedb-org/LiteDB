using System.Collections.Generic;
using System.Linq;
using LiteDB.Vector;

namespace LiteDB.Engine
{
    internal sealed class VectorScoreProjection
    {
        private readonly BsonExpression _fallback;

        internal string Field { get; }
        internal float[] Target { get; }

        internal VectorScoreProjection(string field, float[] target)
        {
            Field = field;
            Target = target.ToArray();
            _fallback = BsonExpression.Create($"VECTOR_SIM({field}, @0)",
                new BsonArray(Target.Select(x => new BsonValue(x))));
        }

        internal IEnumerable<BsonDocument> Project(IEnumerable<BsonDocument> source,
            BsonExpression select, VectorIndexQuery index, Collation collation)
        {
            var useIndexScore = index != null && index.Matches(this);
            var defaultName = select.DefaultFieldName();
            foreach (var document in source)
            {
                var metric = VectorDistanceMetric.Cosine;
                BsonValue score;
                if (useIndexScore && index.TryGetScore(document.RawId, out var cachedScore))
                {
                    score = cachedScore;
                    metric = index.Metric;
                }
                else
                {
                    score = _fallback.ExecuteScalar(document, collation);
                }

                var value = select.ExecuteScalar(document, collation);
                var projected = value.IsDocument ? value.AsDocument : new BsonDocument { [defaultName] = value };

                // Keep metadata outside the user's document, including documents with Score fields.
                yield return new BsonDocument
                {
                    // The envelope carries the shape of its projected Document,
                    // so replacement ReadTransforms retain the unwrapping metadata.
                    IsProjectionValue = !value.IsDocument,
                    ["Document"] = projected,
                    ["Score"] = score,
                    ["Metric"] = (int)metric
                };
            }
        }
    }
}
