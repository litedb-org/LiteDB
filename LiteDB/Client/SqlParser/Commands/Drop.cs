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
        /// DROP INDEX {collection}.{indexName}
        /// DROP COLLECTION {collection}
        /// </summary>
        private BsonDataReader ParseDrop()
        {
            _tokenizer.ReadToken().Expect("DROP");

            var token = _tokenizer.ReadToken().Expect(TokenType.Word);

            if (token.Is("INDEX"))
            {
                var collection = _tokenizer.ReadToken().Expect(TokenType.Word).Value;
                _tokenizer.ReadToken().Expect(TokenType.Period);
                var name = _tokenizer.ReadToken().Expect(TokenType.Word).Value;

                _tokenizer.ReadToken().Expect(TokenType.EOF, TokenType.SemiColon);

                var result = _engine.DropIndexAsync(collection, name).ConfigureAwait(false).GetAwaiter().GetResult();

                return new BsonDataReader(result);
            }
            else if(token.Is("COLLECTION"))
            {
                var collection = _tokenizer.ReadToken().Expect(TokenType.Word).Value;

                _tokenizer.ReadToken().Expect(TokenType.EOF, TokenType.SemiColon);

                var result = _engine.DropCollectionAsync(collection).ConfigureAwait(false).GetAwaiter().GetResult();

                return new BsonDataReader(result);
            }
            else
            {
                throw LiteException.UnexpectedToken(token, "INDEX|COLLECTION");
            }
        }
    }
}