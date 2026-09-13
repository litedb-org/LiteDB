using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace LiteDB
{
    internal partial class BsonExpressionParser
    {
        /// <summary>
        /// Parse expression functions, like MAP, FILTER or SORT.
        /// MAP(items[*] => @.Name)
        /// </summary>
        private static BsonExpression ParseFunction(string functionName, BsonExpressionType type, Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope, bool convertScalarLeftToEnumerable = true, bool isScalarResult = false)
        {
            if (tokenizer.LookAhead().Type != TokenType.OpenParenthesis) return null;

            tokenizer.ReadToken().Expect(TokenType.OpenParenthesis);

            var left = ParseSingleExpression(tokenizer, context, parameters, scope);

            // if left is a scalar expression, convert into enumerable expression (avoid to use [*] all the time)
            if (convertScalarLeftToEnumerable && left.IsScalar)
            {
                left = ConvertToEnumerable(left);
            }

            BsonExpression vectorTarget = null;
            var args = new List<Expression>();
            args.Add(context.Root);
            args.Add(context.Collation);
            args.Add(context.Parameters);

            var src = new StringBuilder(functionName + "(" + left.Source);
            var isImmutable = left.IsImmutable;
            var useSource = left.UseSource;
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            args.Add(left.Expression);
            fields.AddRange(left.Fields);

            // read =>
            if (tokenizer.LookAhead().Type == TokenType.Equals)
            {
                tokenizer.ReadToken().Expect(TokenType.Equals);
                tokenizer.ReadToken().Expect(TokenType.Greater);

                var right = BsonExpression.ParseAndCompile(tokenizer, BsonExpressionParserMode.Full, parameters,
                    left.Type == BsonExpressionType.Source ? DocumentScope.Source : DocumentScope.Current);

                src.Append("=>" + right.Source);
                args.Add(Expression.Constant(right));
                fields.AddRange(right.Fields);
            }

            if (tokenizer.LookAhead().Type != TokenType.CloseParenthesis)
            {
                tokenizer.ReadToken().Expect(TokenType.Comma);

                src.Append(",");

                // try more parameters ,
                while (!tokenizer.CheckEOF())
                {
                    var parameter = ParseFullExpression(tokenizer, context, parameters, scope);

                    if (type == BsonExpressionType.VectorSim && vectorTarget == null) vectorTarget = parameter;
                    if (parameter.IsImmutable == false) isImmutable = false;
                    if (parameter.UseSource) useSource = true;

                    args.Add(parameter.Expression);
                    src.Append(parameter.Source);
                    fields.AddRange(parameter.Fields);

                    if (tokenizer.LookAhead().Type == TokenType.Comma)
                    {
                        src.Append(tokenizer.ReadToken().Value);
                        continue;
                    }
                    break;
                }
            }

            tokenizer.ReadToken().Expect(TokenType.CloseParenthesis);
            src.Append(")");

            // Only VECTOR_SIM carries operands for metric-aware vector planning.
            if (type == BsonExpressionType.VectorSim && (args.Count != 5 || vectorTarget == null))
            {
                throw new LiteException(LiteException.UNEXPECTED_TOKEN, "VECTOR_SIM requires exactly two arguments.");
            }

            var method = BsonExpression.GetFunction(functionName, args.Count - 5);

            return new BsonExpression
            {
                Type = type,
                Left = type == BsonExpressionType.VectorSim ? left : null,
                Right = vectorTarget,
                Parameters = parameters,
                IsImmutable = isImmutable,
                UseSource = useSource,
                IsScalar = isScalarResult,
                Fields = fields,
                Expression = Expression.Call(method, args.ToArray()),
                Source = src.ToString()
            };
        }

    }
}
