using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    internal static class BorrowedAggregateDetector
    {
        public static bool TryDetect(BsonExpression expression,
            out QueryAggregate aggregate, out string fieldName)
        {
            if (!(expression.Expression is MethodCallExpression document) ||
                document.Method.DeclaringType != typeof(BsonExpressionOperators) ||
                document.Method.Name != "DOCUMENT_INIT" || document.Arguments.Count != 2 ||
                !(document.Arguments[0] is NewArrayExpression keys) ||
                !(document.Arguments[1] is NewArrayExpression values) ||
                keys.Expressions.Count != 1 || values.Expressions.Count != 1 ||
                !(keys.Expressions[0] is ConstantExpression key) ||
                !(key.Value is string name) ||
                !(values.Expressions[0] is MethodCallExpression call) ||
                call.Method.DeclaringType != typeof(BsonExpressionMethods) ||
                call.Arguments.Count != 1 || !IsSimpleSource(call.Arguments[0]))
            {
                aggregate = QueryAggregate.None;
                fieldName = null;
                return false;
            }

            if (call.Method.Name == "COUNT")
            {
                aggregate = QueryAggregate.Count;
            }
            else if (call.Method.Name == "ANY")
            {
                aggregate = QueryAggregate.Exists;
            }
            else
            {
                aggregate = QueryAggregate.None;
                fieldName = null;
                return false;
            }

            fieldName = name;
            return true;
        }

        private static bool IsSimpleSource(Expression expression)
        {
            if (expression is ParameterExpression parameter)
            {
                return typeof(IEnumerable<BsonDocument>).IsAssignableFrom(parameter.Type);
            }

            if (expression is MethodCallExpression map &&
                map.Method.DeclaringType == typeof(BsonExpressionFunctions) &&
                map.Method.Name == "MAP" && map.Arguments.Count == 5 &&
                map.Arguments[4] is ConstantExpression selector &&
                selector.Value is BsonExpression bsonSelector &&
                bsonSelector.Type == BsonExpressionType.Path && bsonSelector.IsScalar)
            {
                return IsSimpleSource(map.Arguments[3]);
            }

            return false;
        }
    }
}
