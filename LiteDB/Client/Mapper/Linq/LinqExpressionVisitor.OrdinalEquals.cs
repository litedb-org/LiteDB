using System;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        /// <summary>
        /// Visit :: x => x.Name.`Equals(value, StringComparison.Ordinal)`
        /// </summary>
        private bool TryVisitOrdinalStringEquals(MethodCallExpression node)
        {
            if (!this.IsOrdinalStringEquals(node)) return false;

            // Ordinal-equal strings are identical, hence equal under every collation (the mapper does not
            // know the engine's): the plain comparison can only widen the match, so it restores the index
            // seek while the exact call still decides. No other mode is a subset of an unknown collation.
            this.ResolvePattern(
                node.Method.IsStatic ?
                    "(@0 = @1 AND STRING_EQUALS(@0, @1, @2) = true)" :
                    "(# = @0 AND STRING_EQUALS_INSTANCE(#, @0, @1) = true)",
                node.Object,
                node.Arguments);

            return true;
        }

        private bool IsOrdinalStringEquals(Expression expr)
        {
            if (!(expr is MethodCallExpression node) || node.Method.DeclaringType != typeof(string) ||
                node.Method.Name != nameof(string.Equals) || node.Arguments.Count == 0)
            {
                return false;
            }

            var mode = node.Arguments[node.Arguments.Count - 1];

            return mode.Type == typeof(StringComparison) && !ParameterExpressionVisitor.Test(mode) &&
                StringComparison.Ordinal.Equals(this.Evaluate(mode));
        }
    }
}
