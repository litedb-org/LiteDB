using System;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private bool TryVisitEnumEquals(MethodCallExpression node)
        {
            var declaringType = node.Method.DeclaringType;
            if ((declaringType != typeof(Enum) && declaringType != typeof(object) && declaringType != typeof(ValueType)) ||
                node.Method.Name != nameof(Enum.Equals) ||
                node.Object == null || node.Arguments.Count != 1 || !ParameterExpressionVisitor.Test(node))
            {
                return false;
            }

            var left = UnwrapEnumBoxing(node.Object);
            if (!left.Type.IsEnum) return false;

            var right = UnwrapEnumBoxing(node.Arguments[0]);
            if (!ParameterExpressionVisitor.Test(right))
            {
                var value = this.Evaluate(right);
                if (value == null || value.GetType() != left.Type)
                {
                    this.VisitConstant(Expression.Constant(false));
                    return true;
                }
                right = Expression.Constant(value, left.Type);
            }
            else if (right.Type != left.Type)
            {
                if (!right.Type.IsEnum) return false;

                // Equal numeric values from different enum types are not equal in CLR Enum.Equals.
                this.VisitConstant(Expression.Constant(false));
                return true;
            }

            this.Visit(Expression.Equal(left, right));
            return true;
        }

        private static Expression UnwrapEnumBoxing(Expression expression)
        {
            while (expression is UnaryExpression conversion && conversion.NodeType == ExpressionType.Convert &&
                conversion.Method == null && (conversion.Type == typeof(object) ||
                    conversion.Type == typeof(Enum) || conversion.Type == typeof(ValueType)))
            {
                expression = conversion.Operand;
            }
            return expression;
        }
    }
}
