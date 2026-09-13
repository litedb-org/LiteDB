using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal partial class SqlParser
    {
        /// <summary>
        /// [ EXPLAIN ]
        ///    SELECT {selectExpr}
        ///    [ INTO {newcollection|$function} [ : {autoId} ] ]
        ///    [ FROM {collection|$function} ]
        /// [ INCLUDE {pathExpr0} [, {pathExprN} ]
        ///   [ WHERE {filterExpr} ]
        ///   [ GROUP BY {groupByExpr} ]
        ///  [ HAVING {filterExpr} ]
        ///   [ ORDER BY {orderByExpr} [ ASC | DESC ] ]
        ///   [ LIMIT {number} ]
        ///  [ OFFSET {number} ]
        ///     [ FOR UPDATE ]
        /// </summary>
        private IBsonDataReader ParseSelect()
        {
            // initialize query definition
            var query = new Query();

            var token = _tokenizer.ReadToken();

            query.ExplainPlan = token.Is("EXPLAIN");

            if (query.ExplainPlan) token = _tokenizer.ReadToken();

            token.Expect("SELECT");

            // read required SELECT <expr> and convert into single expression
            query.Select = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.SelectDocument, _parameters);

            // read FROM|INTO
            var from = _tokenizer.ReadToken();

            if (from.Type == TokenType.EOF || from.Type == TokenType.SemiColon)
            {
                // select with no FROM - just run expression (avoid DUAL table, Mr. Oracle)
                //TODO: i think will be better add all sql into engine
                var result = query.Select.Execute(_collation.Value);

                var defaultName = "expr";
                var data = result.Select(x => x.IsDocument ? x.AsDocument : new BsonDocument { [defaultName] = x }).FirstOrDefault();

                return new BsonDataReader(data, null);
            }
            else if (from.Is("INTO"))
            {
                query.Into = ParseCollection(_tokenizer);
                query.IntoAutoId = this.ParseWithAutoId();

                _tokenizer.ReadToken().Expect("FROM");
            }
            else
            {
                from.Expect("FROM");
            }

            // read FROM <name>
            var collection = ParseCollection(_tokenizer);

            var ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("INCLUDE"))
            {
                // read first INCLUDE (before)
                _tokenizer.ReadToken();

                foreach(var path in this.ParseListOfExpressions())
                {
                    query.Includes.Add(path);
                }
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("WHERE"))
            {
                // read WHERE keyword
                _tokenizer.ReadToken();

                var where = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.Full, _parameters);

                query.Where.Add(where);
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("GROUP"))
            {
                // read GROUP BY keyword
                _tokenizer.ReadToken();
                _tokenizer.ReadToken().Expect("BY");

                var groupBy = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.Full, _parameters);

                query.GroupBy = groupBy;

                ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

                if (ahead.Is("HAVING"))
                {
                    // read HAVING keyword
                    _tokenizer.ReadToken();

                    var having = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.Full, _parameters);

                    query.Having = having;
                }
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("ORDER"))
            {
                // read ORDER BY keyword
                _tokenizer.ReadToken();
                _tokenizer.ReadToken().Expect("BY");

                var orderBy = BsonExpression.Create(_tokenizer, BsonExpressionParserMode.Full, _parameters);

                var orderByOrder = Query.Ascending;
                var orderByToken = _tokenizer.LookAhead();

                if (orderByToken.Is("ASC") || orderByToken.Is("DESC"))
                {
                    orderByOrder = _tokenizer.ReadToken().Is("ASC") ? Query.Ascending : Query.Descending;
                }

                query.OrderBy = orderBy;
                query.Order = orderByOrder;
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("LIMIT"))
            {
                // read LIMIT keyword
                _tokenizer.ReadToken();
                var limit = _tokenizer.ReadToken().Expect(TokenType.Int).Value;

                query.Limit = Convert.ToInt32(limit);
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("OFFSET"))
            {
                // read OFFSET keyword
                _tokenizer.ReadToken();
                var offset = _tokenizer.ReadToken().Expect(TokenType.Int).Value;

                query.Offset = Convert.ToInt32(offset);
            }

            ahead = _tokenizer.LookAhead().Expect(TokenType.Word, TokenType.EOF, TokenType.SemiColon);

            if (ahead.Is("FOR"))
            {
                // read FOR keyword
                _tokenizer.ReadToken();
                _tokenizer.ReadToken().Expect("UPDATE");

                query.ForUpdate = true;
            }

            // read eof/;
            _tokenizer.ReadToken().Expect(TokenType.EOF, TokenType.SemiColon);

            return _engine.Query(collection, query);
        }

        /// <summary>
        /// Read collection name and parameter (in case of system collections)
        /// </summary>
        public static string ParseCollection(Tokenizer tokenizer)
        {
            return ParseCollection(tokenizer, out var name, out var options);
        }

        /// <summary>
        /// Read collection name and parameter (in case of system collections)
        /// </summary>
        public static string ParseCollection(Tokenizer tokenizer, out string name, out BsonValue options)
        {
            name = tokenizer.ReadToken().Expect(TokenType.Word).Value;

            // if collection starts with $, check if exist any parameter
            if (name.StartsWith("$"))
            {
                var next = tokenizer.LookAhead();

                if (next.Type == TokenType.OpenParenthesis)
                {
                    tokenizer.ReadToken(); // read (

                    if (tokenizer.LookAhead().Type == TokenType.CloseParenthesis)
                    {
                        options = null;
                    }
                    else
                    {
                        options = new JsonReader(tokenizer).Deserialize();
                    }

                    tokenizer.ReadToken().Expect(TokenType.CloseParenthesis); // read )
                }
                else
                {
                    options = null;
                }
            }
            else
            {
                options = null;
            }

            return name + (options == null ? "" : "(" + JsonSerializer.Serialize(options) + ")");
        }
    }
}