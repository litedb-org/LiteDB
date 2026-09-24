using System.Collections.Generic;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal partial class BsonExpressionParser
    {
        private static BsonExpression ParseFunction(string functionName, BsonExpressionType type,
            Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope,
            bool convertScalarLeftToEnumerable = true, bool isScalarResult = false)
        {
            if (tokenizer.LookAhead().Type != TokenType.OpenParenthesis) return null;
            tokenizer.ReadToken().Expect(TokenType.OpenParenthesis);
            var left = ParseSingleExpression(tokenizer, context, parameters, scope);
            BsonExpression right = null;
            var arguments = new List<BsonExpression>();
            if (tokenizer.LookAhead().Type == TokenType.Equals)
            {
                tokenizer.ReadToken().Expect(TokenType.Equals);
                tokenizer.ReadToken().Expect(TokenType.Greater);
                right = BsonExpression.ParseAndCompile(tokenizer, BsonExpressionParserMode.Full, parameters,
                    left.Type == BsonExpressionType.Source ? DocumentScope.Source : DocumentScope.Current);
            }
            if (tokenizer.LookAhead().Type != TokenType.CloseParenthesis)
            {
                tokenizer.ReadToken().Expect(TokenType.Comma);
                while (!tokenizer.CheckEOF())
                {
                    arguments.Add(ParseFullExpression(tokenizer, context, parameters, scope));
                    if (tokenizer.LookAhead().Type != TokenType.Comma) break;
                    tokenizer.ReadToken();
                }
            }
            tokenizer.ReadToken().Expect(TokenType.CloseParenthesis);
            return Function(functionName, type, left, right, arguments, context, parameters,
                convertScalarLeftToEnumerable, isScalarResult);
        }
    }
}
