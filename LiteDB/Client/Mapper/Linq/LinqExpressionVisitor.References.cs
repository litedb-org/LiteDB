using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, Type> _lambdaReferences = new Dictionary<ParameterExpression, Type>();

        private Expression VisitLambdaWithReferences<T>(Expression<T> node)
        {
            var previous = node.Parameters.Select(parameter =>
            {
                var present = _lambdaReferences.TryGetValue(parameter, out var type);
                return new { Parameter = parameter, Present = present, Type = type };
            }).ToArray();
            foreach (var parameter in node.Parameters) _lambdaReferences[parameter] = _dbRefType;
            try
            {
                var result = base.VisitLambda(node);
                // ExpressionVisitor also visits the declaration after the body.
                _builder.Length--;
                return result;
            }
            finally
            {
                foreach (var entry in previous)
                {
                    if (entry.Present) _lambdaReferences[entry.Parameter] = entry.Type;
                    else _lambdaReferences.Remove(entry.Parameter);
                }
            }
        }

        private Type GetEnumerableReferenceType(Expression source)
        {
            while (source is UnaryExpression conversion && conversion.Method == null &&
                (conversion.NodeType == ExpressionType.Convert || conversion.NodeType == ExpressionType.TypeAs))
            {
                source = conversion.Operand;
            }
            if (!ParameterExpressionVisitor.Test(source)) return null;

            if (source is MemberExpression member && member.Expression != null)
            {
                var owner = member.Expression;
                while (owner is UnaryExpression conversion && conversion.Method == null &&
                    (conversion.NodeType == ExpressionType.Convert || conversion.NodeType == ExpressionType.TypeAs) &&
                    conversion.Type.IsAssignableFrom(conversion.Operand.Type))
                {
                    owner = conversion.Operand;
                }
                var entity = _mapper.GetEntityMapper(owner.Type);
                entity.WaitForInitialization();
                var field = entity.FindMember(member.Member);
                return field?.IsDbRef == true ? field.UnderlyingType : null;
            }
            if (source is MethodCallExpression method && method.Method.DeclaringType == typeof(Enumerable))
            {
                switch (method.Method.Name)
                {
                    case "Where":
                    case "AsEnumerable":
                    case "ToList":
                    case "ToArray": return this.GetEnumerableReferenceType(method.Arguments[0]);
                    case "Select":
                        if (method.Arguments[1] is LambdaExpression selector)
                        {
                            return selector.Body == selector.Parameters[0]
                                ? this.GetEnumerableReferenceType(method.Arguments[0])
                                : this.GetEnumerableReferenceType(selector.Body);
                        }
                        break;
                }
            }
            return null;
        }
    }
}
