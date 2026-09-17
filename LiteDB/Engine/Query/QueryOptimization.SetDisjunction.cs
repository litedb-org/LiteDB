using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private static readonly HashSet<MethodInfo> _unionArithmetic = new HashSet<MethodInfo>
        {
            BsonExpressionFactory.Operators["+"].Item2, BsonExpressionFactory.Operators["-"].Item2,
            BsonExpressionFactory.Operators["*"].Item2, BsonExpressionFactory.Operators["/"].Item2,
            BsonExpressionFactory.Operators["%"].Item2
        };

        private static bool TryGetUnionConstraint(BsonExpression expression, out BsonExpression field,
            out BsonExpression value, out BsonExpressionType operation)
        {
            if (TryGetScalarBound(expression, out field, out value, out operation, allowComputedValue: true)) return true;
            if (operation == BsonExpressionType.Equal && expression.IsANY && expression.Left?.IsScalar == false && expression.Right?.IsScalar == true)
            {
                // A constant sequence ANY = scalar field is scalar membership.
                // Retain its ITEMS evaluation: a binary parameter enumerates bytes,
                // whereas scalar IN with a binary parameter compares the whole blob.
                field = expression.Right;
                value = expression.Left;
                operation = BsonExpressionType.In;
            }
            else if ((operation != BsonExpressionType.In && operation != BsonExpressionType.Between) || field?.IsScalar != true)
            {
                return false;
            }
            return field.IsScalar && field.IsImmutable && !field.IsVolatile && !field.IsValue &&
                value.IsValue && !value.UseSource && !value.IsVolatile;
        }

        private static bool IsUnionValueExpression(Expression expression, ref int budget)
        {
            if (--budget < 0) return false;
            if (expression is ConstantExpression constant) return constant.Value is BsonValue;
            if (!(expression is MethodCallExpression call)) return false;
            if (call.Method == BsonExpressionFactory._parameterPathMethod)
                return call.Arguments[0] is ParameterExpression parameter && parameter.Type == typeof(BsonDocument) &&
                    call.Arguments[1] is ConstantExpression;
            if (call.Method == BsonExpressionFactory._itemsMethod)
                return IsUnionValueExpression(call.Arguments[0], ref budget);
            if (_unionArithmetic.Contains(call.Method))
                return IsUnionValueExpression(call.Arguments[0], ref budget) && IsUnionValueExpression(call.Arguments[1], ref budget);
            if (call.Method != BsonExpressionFactory._arrayInitMethod || !(call.Arguments[0] is NewArrayExpression array)) return false;
            foreach (var item in array.Expressions)
                if (!IsUnionValueExpression(item, ref budget)) return false;
            return true;
        }

        private bool? _hasCheaperScalarEquality;

        private bool HasCheaperScalarEquality(CollectionIndex[] indexes)
        {
            // A nonempty range costs at least 20; equality seeks cost at most 10.
            // Avoid expanding IN parameters for a more expensive candidate. Even
            // proving an empty set can cost more than the existing equality seek.
            if (_hasCheaperScalarEquality.HasValue) return _hasCheaperScalarEquality.Value;
            // Terms and index metadata are fixed for this QueryOptimization instance.
            _hasCheaperScalarEquality = false;
            if (_terms.Count < 2) return false;
            foreach (var term in _terms)
            {
                if (term.Type == BsonExpressionType.Equal && term.Left.IsScalar && term.Right.IsScalar &&
                    FindPredicateIndex(indexes, term, out _) != null)
                {
                    _hasCheaperScalarEquality = true;
                    return true;
                }
            }
            return false;
        }
    }
}
