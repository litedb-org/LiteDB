using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression Constant(BsonValue value, BsonDocument parameters)
        {
            var type = value.IsString ? BsonExpressionType.String :
                value.IsBoolean ? BsonExpressionType.Boolean :
                value.IsNull ? BsonExpressionType.Null :
                value.IsDouble ? BsonExpressionType.Double : BsonExpressionType.Int;
            return new BsonExpression
            {
                Type = type,
                Parameters = parameters,
                IsImmutable = true,
                IsScalar = true,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Expression = Expression.Constant(value),
                Source = BsonExpressionFormatter.Constant(value)
            };
        }

        internal static BsonExpression Parameter(string name, ExpressionContext context, BsonDocument parameters)
        {
            return new BsonExpression
            {
                Type = BsonExpressionType.Parameter,
                Parameters = parameters,
                IsImmutable = false,
                IsScalar = true,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Expression = Expression.Call(_parameterPathMethod, context.Parameters, Expression.Constant(name)),
                Source = "@" + name
            };
        }

        internal static BsonExpression Path(string field, bool root, DocumentScope scope,
            ExpressionContext context, BsonDocument parameters)
        {
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // At the outer scope @ is also the input document. Inside MAP/FILTER it
            // denotes a local item instead and must not add root document fields.
            if (root || scope != DocumentScope.Current) fields.Add(field.Length == 0 ? "$" : field);
            return new BsonExpression
            {
                Type = BsonExpressionType.Path,
                Parameters = parameters,
                IsImmutable = true,
                IsScalar = true,
                Fields = fields,
                Expression = Expression.Call(_memberPathMethod, root ? context.Root : context.Current, Expression.Constant(field)),
                Source = (root ? "$" : "@") + (field.Length == 0 ? "" : "." + BsonExpressionFormatter.PathField(field))
            };
        }

        internal static BsonExpression Member(BsonExpression target, string field, DocumentScope scope,
            ExpressionContext context)
        {
            if (target.Source == "$" || target.Source == "@")
            {
                return Path(field, target.Source == "$", scope, context, target.Parameters);
            }
            var result = Copy(target);
            result.Expression = Expression.Call(_memberPathMethod, target.Expression, Expression.Constant(field));
            result.Source = target.Source + "." + BsonExpressionFormatter.PathField(field);
            return result;
        }

        internal static BsonExpression Index(BsonExpression target, int index, BsonExpression parameter,
            ExpressionContext context, string indexSource = null)
        {
            var result = Copy(target);
            result.Expression = Expression.Call(_arrayIndexMethod, target.Expression, Expression.Constant(index),
                NestedTemplate(parameter), context.Root, context.Collation, context.Parameters);
            result.Source = target.Source + "[" + (parameter?.Source ?? indexSource ?? index.ToString(CultureInfo.InvariantCulture)) + "]";
            if (parameter != null)
            {
                result.IsImmutable &= parameter.IsImmutable;
                result.IsVolatile |= parameter.IsVolatile;
                result.UseSource |= parameter.UseSource;
                result.Fields = new HashSet<string>(target.Fields, StringComparer.OrdinalIgnoreCase).AddRange(parameter.Fields);
            }
            return result;
        }

        internal static BsonExpression Source(ExpressionContext context, BsonDocument parameters)
        {
            return new BsonExpression
            {
                Type = BsonExpressionType.Source, Parameters = parameters, IsImmutable = true,
                UseSource = true, IsScalar = false,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "$" },
                Expression = context.Source, Source = "*"
            };
        }

        internal static BsonExpression Array(IEnumerable<BsonExpression> items, BsonDocument parameters)
        {
            var values = items.Select(x => x.IsScalar ? x : ConvertToArray(x)).ToArray();
            return Aggregate(BsonExpressionType.Array, values, parameters,
                Expression.Call(_arrayInitMethod, Expression.NewArrayInit(typeof(BsonValue), values.Select(x => x.Expression))),
                BsonExpressionFormatter.Array(values));
        }

        internal static BsonExpression Document(IEnumerable<KeyValuePair<string, BsonExpression>> members, BsonDocument parameters)
        {
            var fields = members.ToArray();
            var values = fields.Select(x => x.Value.IsScalar ? x.Value : ConvertToArray(x.Value)).ToArray();
            return Aggregate(BsonExpressionType.Document, values, parameters,
                Expression.Call(_documentInitMethod,
                    Expression.NewArrayInit(typeof(string), fields.Select(x => Expression.Constant(x.Key))),
                    Expression.NewArrayInit(typeof(BsonValue), values.Select(x => x.Expression))),
                BsonExpressionFormatter.Document(fields, values));
        }

        private static BsonExpression Aggregate(BsonExpressionType type, BsonExpression[] children,
            BsonDocument parameters, Expression expression, string source)
        {
            return new BsonExpression
            {
                Type = type, Parameters = parameters,
                IsImmutable = children.All(x => x.IsImmutable), UseSource = children.Any(x => x.UseSource),
                IsVolatile = children.Any(x => x.IsVolatile),
                IsScalar = true,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(children.SelectMany(x => x.Fields)),
                Expression = expression, Source = source
            };
        }

        internal static BsonExpression Group(BsonExpression inner)
        {
            var result = Copy(inner);
            result.Source = "(" + inner.Source + ")";
            return result;
        }

        private static BsonExpression Copy(BsonExpression expression)
        {
            return new BsonExpression
            {
                Type = expression.Type, Parameters = expression.Parameters,
                IsImmutable = expression.IsImmutable, UseSource = expression.UseSource, IsVolatile = expression.IsVolatile,
                IsScalar = expression.IsScalar, IsANY = expression.IsANY, Fields = expression.Fields,
                Expression = expression.Expression, Left = expression.Left, Right = expression.Right,
                Source = expression.Source
            };
        }
    }
}
