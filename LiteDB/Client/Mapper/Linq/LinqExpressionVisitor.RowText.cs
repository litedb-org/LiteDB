using System;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private const string StringSearchPrefix = "STRING_";
        private const string StringIndexOfPrefix = "STRING_INDEXOF(";

        /// <summary>
        /// Visit :: x => x.Name.StartsWith(`x.Other`, mode). Text read from the row can be null, missing or not a string
        /// in some rows; such a row does not match. A constant text keeps the CLR argument errors.
        /// </summary>
        private static string GuardRowSuppliedText(MethodCallExpression node, string pattern)
        {
            var isSearch = node.Method.DeclaringType == typeof(string) && !node.Method.IsStatic &&
                pattern.StartsWith(StringSearchPrefix, StringComparison.Ordinal) && node.Arguments.Count > 0;

            if (!isSearch || !ParameterExpressionVisitor.Test(node.Arguments[0])) return pattern;

            // AND and IIF evaluate lazily, so the search never sees the unusable text
            return pattern.StartsWith(StringIndexOfPrefix, StringComparison.Ordinal) ?
                "IIF(IS_STRING(@0), " + pattern + ", null)" :
                "(IS_STRING(@0) = true AND " + pattern + " = true)";
        }
    }
}
