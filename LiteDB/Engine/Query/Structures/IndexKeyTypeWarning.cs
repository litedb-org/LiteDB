using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    /// <summary>
    /// Explains an equality seek that cannot match because every key of the index has another type than the value.
    /// Reads index nodes, so it belongs to EXPLAIN only - never to query execution
    /// </summary>
    internal static class IndexKeyTypeWarning
    {
        private const string NumericFamily = "numeric";

        public static string Find(Index query, CollectionPage collection, IndexService indexer)
        {
            var values = GetSeekValues(query);
            var index = values == null ? null : collection?.GetCollectionIndex(query.Name);

            if (index == null) return null;

            // keys are ordered by type before value, so two ends of one family leave no room for any other key.
            // A Null key is a family of its own: a field that is missing in some documents proves nothing
            var first = indexer.FindAll(index, Query.Ascending).FirstOrDefault();
            var last = indexer.FindAll(index, Query.Descending).FirstOrDefault();

            if (first == null || last == null) return null;

            var family = GetFamily(first.Key);

            if (family != GetFamily(last.Key) || values.Any(x => GetFamily(x) == family)) return null;

            var types = values.Select(x => x.Type.ToString()).Distinct().ToArray();

            return string.Format("index '{0}' holds {1} keys; the {2} {3} cannot match",
                index.Name,
                family,
                string.Join(", ", types),
                types.Length == 1 ? "value" : "values");
        }

        private static ICollection<BsonValue> GetSeekValues(Index query)
        {
            if (query is IndexEquals equals) return new[] { equals.Value };
            if (query is IndexIn list && list.Values.Count > 0) return list.Values;

            return null;
        }

        /// <summary>
        /// Numbers compare by value across Int32/Int64/Double/Decimal, every other type only equals itself
        /// </summary>
        private static string GetFamily(BsonValue value)
        {
            return value.IsNumber ? NumericFamily : value.Type.ToString();
        }
    }
}
