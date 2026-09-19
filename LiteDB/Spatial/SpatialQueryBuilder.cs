using System.Collections.Generic;
using LiteDB;

namespace LiteDB.Spatial
{
    internal static class SpatialQueryBuilder
    {
        public static BsonExpression BuildRangePredicate(IReadOnlyList<(long Start, long End)> ranges)
        {
            if (ranges == null || ranges.Count == 0)
            {
                return null;
            }

            var expressions = new List<BsonExpression>(ranges.Count);

            foreach (var range in ranges)
            {
                expressions.Add(Query.Between("$._gh", new BsonValue(range.Start), new BsonValue(range.End)));
            }

            return CombineOr(expressions);
        }

        public static BsonExpression BuildBoundingBoxPredicate(GeoBoundingBox box)
        {
            if (box.MaxLon < box.MinLon)
            {
                return null;
            }

            var parameters = new[]
            {
                new BsonValue(box.MaxLat),
                new BsonValue(box.MinLat),
                new BsonValue(box.MaxLon),
                new BsonValue(box.MinLon)
            };

            const string predicate = "($._mbb != null) AND $._mbb[0] <= @0 AND $._mbb[2] >= @1 AND $._mbb[1] <= @2 AND $._mbb[3] >= @3";

            return BsonExpression.Create(predicate, parameters);
        }

        public static BsonExpression CombineSpatialPredicates(BsonExpression rangeExpression, BsonExpression boundingExpression)
        {
            if (rangeExpression == null)
            {
                return boundingExpression;
            }

            if (boundingExpression == null)
            {
                return rangeExpression;
            }

            var parameters = new BsonDocument();

            if (boundingExpression.Parameters != null)
            {
                foreach (var parameter in boundingExpression.Parameters)
                {
                    parameters[parameter.Key] = parameter.Value;
                }
            }

            var source = $"({rangeExpression.Source}) AND ({boundingExpression.Source})";

            return BsonExpression.Create(source, parameters);
        }

        private static BsonExpression CombineOr(IReadOnlyList<BsonExpression> expressions)
        {
            if (expressions.Count == 0)
            {
                return null;
            }

            if (expressions.Count == 1)
            {
                return expressions[0];
            }

            var buffer = new BsonExpression[expressions.Count];

            for (var i = 0; i < expressions.Count; i++)
            {
                buffer[i] = expressions[i];
            }

            return Query.Or(buffer);
        }
    }
}
