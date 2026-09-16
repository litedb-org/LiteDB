using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private static bool IncludeChangesIndex(BsonExpression include, BsonExpression index)
        {
            if (!index.Fields.Contains("$") && !include.Fields.Overlaps(index.Fields)) return false;

            var includedPath = new List<string>();
            var indexedPath = new List<string>();
            if (!TryGetMemberPath(include.Expression, "root", includedPath) ||
                !TryGetMemberPath(index.Expression, "root", indexedPath)) return true;

            var common = Math.Min(includedPath.Count, indexedPath.Count);
            for (var i = 0; i < common; i++)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(includedPath[i], indexedPath[i])) return false;
            }

            // Include retains the stored reference identifier. Ancestors and all
            // other descendants can change when a reference is resolved.
            return indexedPath.Count != includedPath.Count + 1 ||
                !StringComparer.OrdinalIgnoreCase.Equals(indexedPath[includedPath.Count], "$id");
        }

        private static bool TryGetMemberPath(Expression expression, string scope, List<string> path)
        {
            if (expression is ParameterExpression parameter) return parameter.Name == scope;
            if (!(expression is MethodCallExpression call) || (call.Method.DeclaringType != typeof(BsonExpressionFunctions) &&
                call.Method.DeclaringType != typeof(BsonExpressionOperators))) return false;

            if (call.Method.Name == "MEMBER_PATH" && call.Arguments[1] is ConstantExpression field && field.Value is string name)
            {
                if (!TryGetMemberPath(call.Arguments[0], scope, path)) return false;
                if (name.Length > 0) path.Add(name);
                return true;
            }

            // Fixed/wildcard array navigation does not change field ancestry.
            // Filtered/dynamic array expressions are conservatively excluded.
            if (call.Method.Name == "ARRAY_INDEX" && call.Arguments[2] is ConstantExpression selector &&
                selector.Value is BsonExpression selection && selection.Expression == null)
            {
                return TryGetMemberPath(call.Arguments[0], scope, path);
            }

            if (call.Method.Name == "ARRAY_FILTER" && call.Arguments[1] is ConstantExpression position &&
                position.Value is int index && index == int.MaxValue)
            {
                return TryGetMemberPath(call.Arguments[0], scope, path);
            }

            if (call.Method.Name == "MAP" && call.Arguments[4] is ConstantExpression mapping && mapping.Value is BsonExpression body)
            {
                return TryGetMemberPath(call.Arguments[3], scope, path) &&
                    TryGetMemberPath(body.Expression, "current", path);
            }

            return false;
        }
    }
}
