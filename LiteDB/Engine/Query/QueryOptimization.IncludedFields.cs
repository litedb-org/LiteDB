namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private CollectionIndex FindStoredIndex(CollectionIndex[] indexes, BsonExpression expression)
        {
            foreach (var index in indexes)
                if (MatchesStoredIndex(index.Expression, expression)) return index;
            return null;
        }

        private bool MatchesStoredIndex(string indexSource, BsonExpression expression)
        {
            if (!IndexExpressionIdentity.Matches(indexSource, expression)) return false;
            foreach (var include in _query.Includes)
            {
                // INCLUDE replaces members of a stored reference. Its index keys
                // cannot filter or order the resolved values. Disjoint member paths
                // remain usable; computed/array paths conservatively share a root.
                if (SharesIncludedRoot(expression, include) &&
                    IndexExpressionIdentity.PathsMayOverlap(expression, include)) return false;
            }
            return true;
        }

        private static bool SharesIncludedRoot(BsonExpression expression, BsonExpression include)
        {
            if (expression.Fields.Contains("$") || include.Fields.Contains("$")) return true;
            foreach (var field in expression.Fields)
                if (include.Fields.Contains(field)) return true;
            return false;
        }

        private bool IsIncludedRootField(string field)
        {
            foreach (var include in _query.Includes)
                if (field == "$" || include.Fields.Contains("$") || include.Fields.Contains(field)) return true;
            return false;
        }
    }
}
