using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    // Recognize row cardinality from the shared expression tree, without parsing
    // Source or replacing the existing semantics of arbitrary aggregate expressions.
    internal sealed class RowAggregate
    {
        private readonly string[] _names;
        private readonly bool[] _counts;
        internal bool NeedsCount { get; }

        private RowAggregate(string[] names, bool[] counts)
        {
            _names = names;
            _counts = counts;
            foreach (var count in counts) NeedsCount |= count;
        }

        internal static RowAggregate TryCreate(BsonExpression select)
        {
            if (select.Expression is MethodCallExpression document && document.Method == BsonExpressionFactory._documentInitMethod)
            {
                var names = (NewArrayExpression)document.Arguments[0];
                var values = (NewArrayExpression)document.Arguments[1];
                if (values.Expressions.Count == 0) return null;
                var columns = new string[values.Expressions.Count];
                var counts = new bool[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                {
                    if (!TryGetAggregate(values.Expressions[i], out counts[i])) return null;
                    columns[i] = (string)((ConstantExpression)names.Expressions[i]).Value;
                }
                return new RowAggregate(columns, counts);
            }
            return TryGetAggregate(select.Expression, out var count) ?
                new RowAggregate(new[] { select.DefaultFieldName() }, new[] { count }) : null;
        }

        private static bool TryGetAggregate(Expression expression, out bool count)
        {
            count = false;
            if (!(expression is MethodCallExpression call) || call.Method.DeclaringType != typeof(BsonExpressionMethods) ||
                call.Arguments.Count != 1) return false;
            count = call.Method.Name == nameof(BsonExpressionMethods.COUNT);
            if (!count && call.Method.Name != nameof(BsonExpressionMethods.ANY)) return false;
            var input = call.Arguments[0];
            if (IsSource(input)) return true;
            if (!(input is MethodCallExpression map) || map.Method.DeclaringType != typeof(BsonExpressionFunctions) ||
                map.Method.Name != nameof(BsonExpressionFunctions.MAP) || map.Arguments.Count != 5 || !IsSource(map.Arguments[3])) return false;
            var selector = (map.Arguments[4] as ConstantExpression)?.Value as BsonExpression;
            // A scalar member path emits one value per document, including missing/null
            // fields. Other MAP expressions may throw, be volatile, or emit many values.
            return selector != null && selector.IsScalar && IsCurrentPath(selector.Expression);
        }

        private static bool IsSource(Expression expression) => expression is ParameterExpression parameter &&
            parameter.Type == typeof(IEnumerable<BsonDocument>);

        private static bool IsCurrentPath(Expression expression)
        {
            if (expression is ParameterExpression parameter) return parameter.Type == typeof(BsonValue);
            return expression is MethodCallExpression member && member.Method == BsonExpressionFactory._memberPathMethod &&
                member.Arguments[1] is ConstantExpression && IsCurrentPath(member.Arguments[0]);
        }

        internal BsonDocument Project(int count)
        {
            var result = new BsonDocument();
            for (var i = 0; i < _names.Length; i++)
                result[_names[i]] = _counts[i] ? new BsonValue(count) : new BsonValue(count != 0);
            return result;
        }
    }
}
