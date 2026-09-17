namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private bool MatchesStoredIndex(string indexSource, BsonExpression expression)
        {
            if (!IndexExpressionIdentity.Matches(indexSource, expression)) return false;
            foreach (var include in _query.Includes)
            {
                // INCLUDE replaces members of a stored reference. Its index keys
                // cannot filter or order the resolved values. Disjoint member paths
                // remain usable; computed/array paths conservatively share a root.
                if ((expression.Fields.Contains("$") || include.Fields.Contains("$") || expression.Fields.Overlaps(include.Fields)) &&
                    IndexExpressionIdentity.PathsMayOverlap(expression, include)) return false;
            }
            return true;
        }

        private bool IsIncludedRootField(string field)
        {
            foreach (var include in _query.Includes)
                if (field == "$" || include.Fields.Contains("$") || include.Fields.Contains(field)) return true;
            return false;
        }
    }
}
