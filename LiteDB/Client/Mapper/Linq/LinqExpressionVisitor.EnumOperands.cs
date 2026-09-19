using System;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        /// <summary>
        /// Visit :: x => `(int)x.Kind == x.Number` while enums are stored by name. Dropping the conversion would compare
        /// the stored name with a number and silently match nothing, so only operands of the same enum are translated.
        /// </summary>
        private Expression EnsureNameStoredEnumOperand(BinaryExpression node)
        {
            if (_mapper.EnumAsInteger || !ParameterExpressionVisitor.Test(node.Right)) return node.Right;

            if (GetConvertedEnum(node.Left) != GetConvertedEnum(node.Right))
            {
                throw new NotSupportedException(
                    $"Enums are stored by name, so `{node}` cannot compare an enum with a numeric member. Compare members of the same enum type or set BsonMapper.EnumAsInteger.");
            }

            return node.Right;
        }

        /// <summary>
        /// The enum a conversion chain starts from: `(int)x.Kind` is Kind, while `(int)(Kind)x.Number` is a number
        /// wearing an enum cast and yields null, like any other non-enum operand.
        /// </summary>
        private static Type GetConvertedEnum(Expression expr)
        {
            if (expr.NodeType != ExpressionType.Convert && expr.NodeType != ExpressionType.ConvertChecked) return null;

            while (expr.NodeType == ExpressionType.Convert || expr.NodeType == ExpressionType.ConvertChecked)
            {
                expr = ((UnaryExpression)expr).Operand;
            }

            var type = Nullable.GetUnderlyingType(expr.Type) ?? expr.Type;

            return type.GetTypeInfo().IsEnum ? type : null;
        }
    }
}
