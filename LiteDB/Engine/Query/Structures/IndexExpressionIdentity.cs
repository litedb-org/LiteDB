using System;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    internal static class IndexExpressionIdentity
    {
        internal static bool Matches(string indexSource, BsonExpression expression)
        {
            return expression != null && (indexSource == expression.Source || MatchesMemberPath(indexSource, expression));
        }

        private static bool MatchesMemberPath(string indexSource, BsonExpression expression)
        {
            // Only member names can differ here: every access is a MEMBER_PATH
            // over the document. Computed literals and array selectors need their
            // exact identities even when the expression is scalar and path-shaped.
            return expression.IsScalar && expression.Fields.Count == 1 &&
                string.Equals(indexSource, expression.Source, StringComparison.OrdinalIgnoreCase) &&
                IsMemberPath(expression);
        }

        internal static bool IsMemberPath(BsonExpression field)
        {
            return TryGetMemberDepth(field, out _);
        }

        internal static bool PathsMayOverlap(BsonExpression left, BsonExpression right)
        {
            if (!TryGetMemberDepth(left, out var leftDepth) || !TryGetMemberDepth(right, out var rightDepth)) return true;
            var leftPath = left.Expression;
            var rightPath = right.Expression;
            while (leftDepth > rightDepth) { leftPath = ((MethodCallExpression)leftPath).Arguments[0]; leftDepth--; }
            while (rightDepth > leftDepth) { rightPath = ((MethodCallExpression)rightPath).Arguments[0]; rightDepth--; }
            while (leftDepth-- > 0)
            {
                var leftMember = (MethodCallExpression)leftPath;
                var rightMember = (MethodCallExpression)rightPath;
                if (!string.Equals((string)((ConstantExpression)leftMember.Arguments[1]).Value,
                    (string)((ConstantExpression)rightMember.Arguments[1]).Value, StringComparison.OrdinalIgnoreCase)) return false;
                leftPath = leftMember.Arguments[0];
                rightPath = rightMember.Arguments[0];
            }
            return true;
        }

        private static bool TryGetMemberDepth(BsonExpression field, out int members)
        {
            members = 0;
            if (field.Type != BsonExpressionType.Path) return false;
            var expression = field.Expression;
            while (expression is MethodCallExpression member && member.Method == BsonExpressionFactory._memberPathMethod &&
                member.Arguments[1] is ConstantExpression name && name.Value is string)
            {
                if (++members > 64) return false;
                expression = member.Arguments[0];
            }
            return members != 0 && expression is ParameterExpression parameter &&
                (parameter.Type == typeof(BsonDocument) || parameter.Type == typeof(BsonValue));
        }

        internal static string RootFieldSource(string field) =>
            field == "$" ? null : "$." + BsonExpressionFormatter.PathField(field);
    }
}
