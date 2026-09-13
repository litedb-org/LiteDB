using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal partial class SqlParser
    {
        /// <summary>
        /// CREATE [ UNIQUE ] INDEX {indexName} ON {collection} ({indexExpr})
        /// </summary>
        private BsonDataReader ParseCreate()
        {
            _tokenizer.ReadToken().Expect("CREATE");

            var token = _tokenizer.ReadToken().Expect(TokenType.Word);
            var unique = token.Is("UNIQUE");

            if (unique)
            {
                token = _tokenizer.ReadToken();
            }

            token.Expect("INDEX");

            // read indexName
            var name = _tokenizer.ReadToken().Expect(TokenType.Word).Value;

            _tokenizer.ReadToken().Expect("ON");

            // read collectionName
            var collection = _tokenizer.ReadToken().Expect(TokenType.Word).Value;

            // read (
            _tokenizer.ReadToken().Expect(TokenType.OpenParenthesis);

            // read index expression
            var expr = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.Full, new BsonDocument());

            // read )
            _tokenizer.ReadToken().Expect(TokenType.CloseParenthesis);

            // read EOF or ;
            _tokenizer.ReadToken().Expect(TokenType.EOF, TokenType.SemiColon);

            var result = _engine.EnsureIndex(collection, name, expr, unique);

            return new BsonDataReader(result);
        }
    }
}