using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    public sealed partial class BsonExpression
    {
        // Recognize a deliberately small set of single-pass aggregate shapes.
        // Unknown methods, multiple aggregates and group replay keep their cache.
        internal bool CanStreamAggregateSource => this.IsScalar && SourceUses(this.Expression) == 1;

        private static int SourceUses(Expression expression)
        {
            if (expression is ConstantExpression) return 0;
            if (expression is ParameterExpression parameter)
                return parameter.Type == typeof(IEnumerable<BsonDocument>) ? 1 : 0;
            if (expression is NewArrayExpression array)
                return CombineUses(array.Expressions.Select(SourceUses));
            if (!(expression is MethodCallExpression call)) return -1;
            var uses = CombineUses(call.Arguments.Select(SourceUses));
            if (uses <= 0) return uses;
            if (call.Method.DeclaringType == typeof(BsonExpressionMethods))
            {
                switch (call.Method.Name)
                {
                    case "COUNT": case "SUM": case "AVG": case "MIN": case "MAX":
                    case "FIRST": case "LAST": case "ANY": return uses;
                }
            }
            if (call.Method.DeclaringType == typeof(BsonExpressionOperators) &&
                call.Method.Name == "DOCUMENT_INIT") return uses;
            if (call.Method.DeclaringType == typeof(BsonExpressionFunctions) && call.Method.Name == "MAP" &&
                call.Arguments.Last() is ConstantExpression constant &&
                constant.Value is BsonExpression map && !map.UseSource) return uses;
            return -1;
        }

        private static int CombineUses(IEnumerable<int> uses)
        {
            var total = 0;
            foreach (var count in uses)
            {
                if (count < 0 || (total += count) > 1) return -1;
            }
            return total;
        }
    }
}
