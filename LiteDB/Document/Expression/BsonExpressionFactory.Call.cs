using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression Call(string name, IList<BsonExpression> pars,
            ExpressionContext context, BsonDocument parameters)
        {
            var method = BsonExpression.GetMethod(name, pars.Count);
            if (method == null) throw new NotSupportedException($"Method '{name}' does not exist or contains invalid parameters");
            var source = BsonExpressionFormatter.Call(name, pars);
            var isImmutable = pars.All(x => x.IsImmutable);
            var isVolatile = pars.Any(x => x.IsVolatile);
            var useSource = pars.Any(x => x.UseSource);
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(pars.SelectMany(x => x.Fields));
            // test if method are decorated with "Variable" (immutable = false)
            if (method.GetCustomAttribute<VolatileAttribute>() != null)
            {
                isImmutable = false;
                isVolatile = true;
            }

            // method call arguments
            var args = new List<Expression>();

            if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Collation))
            {
                args.Add(context.Collation);
            }

            // getting linq expression from BsonExpression for all parameters
            foreach (var item in method.GetParameters().Where(x => x.ParameterType != typeof(Collation)).Zip(pars, (parameter, expr) => new { parameter, expr }))
            {
                if (item.parameter.ParameterType.IsEnumerable() == false && item.expr.IsScalar == false)
                {
                    // convert enumerable expresion into scalar expression
                    args.Add(ConvertToArray(item.expr).Expression);
                }
                else if (item.parameter.ParameterType.IsEnumerable() && item.expr.IsScalar)
                {
                    // convert scalar expression into enumerable expression
                    args.Add(ConvertToEnumerable(item.expr).Expression);
                }
                else
                {
                    args.Add(item.expr.Expression);
                }
            }

            // special IIF case
            if (method.Name == "IIF" && pars.Count == 3) return CreateConditionalExpression(pars[0], pars[1], pars[2]);

            return new BsonExpression
            {
                Type = BsonExpressionType.Call,
                Parameters = parameters,
                IsImmutable = isImmutable, IsVolatile = isVolatile,
                UseSource = useSource,
                IsScalar = method.ReturnType.IsEnumerable() == false,
                Fields = fields,
                Expression = Expression.Call(method, args.ToArray()),
                Source = source
            };
        }
    }
}
