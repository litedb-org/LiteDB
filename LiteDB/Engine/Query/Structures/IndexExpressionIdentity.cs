using System;
using System.Linq;

namespace LiteDB.Engine
{
    internal static class IndexExpressionIdentity
    {
        internal static bool Matches(string indexSource, BsonExpression expression)
        {
            return expression != null && (indexSource == expression.Source || MatchesRootField(indexSource, expression));
        }

        private static bool MatchesRootField(string indexSource, BsonExpression expression)
        {
            // BSON document field lookup ignores case. Apply that identity only
            // after proving a canonical scalar root field; arbitrary expression
            // text can contain case-sensitive string literals or executable paths.
            return expression.Type == BsonExpressionType.Path && expression.IsScalar &&
                string.Equals(indexSource, expression.Source, StringComparison.OrdinalIgnoreCase) &&
                expression.Fields.Count == 1 && expression.Source == RootFieldSource(expression.Fields.First());
        }

        internal static string RootFieldSource(string field) =>
            field == "$" ? null : "$." + BsonExpressionFormatter.PathField(field);
    }
}
