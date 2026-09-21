using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal enum BsonExpressionParserMode { Full, Single, SelectDocument, UpdateDocument }

    /// <summary>
    /// Compile and execute simple expressions using BsonDocuments. Used in indexes and updates operations. See https://github.com/mbdavid/LiteDB/wiki/Expressions
    /// </summary>
    internal partial class BsonExpressionParser
    {
        #region Operators quick access

        #endregion

        /// <summary>
        /// Start parse string into linq expression. Read path, function or base type bson values (int, double, bool, string)
        /// </summary>
        public static BsonExpression ParseFullExpression(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            var first = ParseSingleExpression(tokenizer, context, parameters, scope);
            var values = new List<BsonExpression> { first };
            var ops = new List<string>();

            // read all blocks and operation first
            while (!tokenizer.EOF)
            {
                // read operator between expressions
                var op = ReadOperant(tokenizer);

                if (op == null) break;

                var expr = ParseSingleExpression(tokenizer, context, parameters, scope);

                // special BETWEEN "AND" read
                if (op.EndsWith("BETWEEN", StringComparison.OrdinalIgnoreCase))
                {
                    var and = tokenizer.ReadToken(true).Expect("AND");

                    var expr2 = ParseSingleExpression(tokenizer, context, parameters, scope);

                    // convert expr and expr2 into an array with 2 values
                    expr = NewArray(expr, expr2);
                }

                values.Add(expr);
                ops.Add(op.ToUpperInvariant());
            }

            var order = 0;

            // now, process operator in correct order
            while (values.Count >= 2)
            {
                var op = Operators.ElementAt(order);
                var n = ops.IndexOf(op.Key);

                if (n == -1)
                {
                    order++;
                }
                else
                {
                    // get left/right values to execute operator
                    var left = values.ElementAt(n);
                    var right = values.ElementAt(n + 1);

                    var result = Binary(op.Key, left, right, context, parameters);

                    // remove left+right and insert result
                    values.Insert(n, result);
                    values.RemoveRange(n + 1, 2);

                    // remove operation
                    ops.RemoveAt(n);
                }
            }

            return values.Single();
        }

        /// <summary>
        /// Start parse string into linq expression. Read path, function or base type bson values (int, double, bool, string)
        /// </summary>
        public static BsonExpression ParseSingleExpression(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            // read next token and test with all expression parts
            var token = tokenizer.ReadToken();

            return
                TryParseDouble(tokenizer, parameters) ??
                TryParseInt(tokenizer, parameters) ??
                TryParseBool(tokenizer, parameters) ??
                TryParseNull(tokenizer, parameters) ??
                TryParseString(tokenizer, parameters) ??
                TryParseSource(tokenizer, context, parameters, scope) ??
                TryParseDocument(tokenizer, context, parameters, scope) ??
                TryParseArray(tokenizer, context, parameters, scope) ??
                TryParseParameter(tokenizer, context, parameters, scope) ??
                TryParseInnerExpression(tokenizer, context, parameters, scope) ??
                TryParseFunction(tokenizer, context, parameters, scope) ??
                TryParseMethodCall(tokenizer, context, parameters, scope) ??
                TryParsePath(tokenizer, context, parameters, scope) ??
                throw LiteException.UnexpectedToken(token);
        }

        /// <summary>
        /// Parse a document builder syntax used in SELECT statment: {expr0} [AS] [{alias}], {expr1} [AS] [{alias}], ...
        /// </summary>
        public static BsonExpression ParseSelectDocumentBuilder(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters)
        {
            var fields = new List<KeyValuePair<string, BsonExpression>>();
            var aliases = new List<KeyValuePair<string, BsonExpression>>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var counter = 1;

            bool stop(Token t) => t.Is("FROM") || t.Is("INTO") || t.Type == TokenType.EOF || t.Type == TokenType.SemiColon;

            void Add(string alias, BsonExpression expr, bool explicitAlias = false)
            {
                var baseAlias = alias;
                while (names.Contains(alias)) alias = baseAlias + counter++;

                names.Add(alias);

                if (!expr.IsScalar) expr = ConvertToArray(expr);

                fields.Add(new KeyValuePair<string, BsonExpression>(alias, expr));
                if (explicitAlias) aliases.Add(fields.Last());
            };

            while (true)
            {
                var expr = ParseFullExpression(tokenizer, context, parameters, DocumentScope.Root);

                var next = tokenizer.LookAhead();

                if (stop(next))
                {
                    Add(expr.DefaultFieldName(), expr);

                    break;
                }
                // field with no alias
                if (next.Type == TokenType.Comma)
                {
                    tokenizer.ReadToken(); // consume ,

                    Add(expr.DefaultFieldName(), expr);
                }
                // using alias
                else
                {
                    if (next.Is("AS"))
                    {
                        tokenizer.ReadToken(); // consume "AS"
                    }

                    var alias = tokenizer.ReadToken().Expect(TokenType.Word);

                    Add(alias.Value, expr, true);

                    // go ahead to next token to see if last field
                    next = tokenizer.LookAhead();

                    if (stop(next))
                    {
                        break;
                    }

                    // consume ,
                    tokenizer.ReadToken().Expect(TokenType.Comma);
                }
            }

            var first = fields[0].Value;

            if (fields.Count == 1 && aliases.Count == 0)
            {
                // if just $ return empty BsonExpression
                if (first.Type == BsonExpressionType.Path && first.Source == "$") return BsonExpression.Root;

                // if single field already a document
                if (fields.Count == 1 && first.Type == BsonExpressionType.Document) return first;

                // special case: EXTEND method also returns only a document
                if (fields.Count == 1 && first.Type == BsonExpressionType.Call && first.Source.StartsWith("EXTEND")) return first;
            }

            var document = Document(fields, parameters);
            document.SelectAliases = aliases;
            return document;
        }

        /// <summary>
        /// Parse a document builder syntax used in UPDATE statment:
        /// {key0} = {expr0}, .... will be converted into { key: [expr], ... }
        /// {key: value} ... return return a new document
        /// </summary>
        public static BsonExpression ParseUpdateDocumentBuilder(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters)
        {
            var next = tokenizer.LookAhead();

            // if starts with { just return a normal document expression
            if (next.Type == TokenType.OpenBrace)
            {
                tokenizer.ReadToken(); // consume {

                return TryParseDocument(tokenizer, context, parameters, DocumentScope.Root);
            }

            var members = new List<KeyValuePair<string, BsonExpression>>();
            while (!tokenizer.CheckEOF())
            {
                var key = ReadKey(tokenizer);
                tokenizer.ReadToken().Expect(TokenType.Equals);
                var value = ParseFullExpression(tokenizer, context, parameters, DocumentScope.Root);
                members.Add(new KeyValuePair<string, BsonExpression>(key, value));
                if (tokenizer.LookAhead().Type != TokenType.Comma) break;
                tokenizer.ReadToken();
            }
            return Document(members, parameters);
        }

        #region Constants

        /// <summary>
        /// Try parse double number - return null if not double token
        /// </summary>
        private static BsonExpression TryParseDouble(Tokenizer tokenizer, BsonDocument parameters)
        {
            string value = null;

            if (tokenizer.Current.Type == TokenType.Double)
            {
                value = tokenizer.Current.Value;
            }
            else if (tokenizer.Current.Type == TokenType.Minus)
            {
                var ahead = tokenizer.LookAhead(false);

                if (ahead.Type == TokenType.Double)
                {
                    value = "-" + tokenizer.ReadToken().Value;
                }
            }

            if (value != null)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture.NumberFormat);

                return Constant(new BsonValue(number), parameters);
            }

            return null;
        }

        /// <summary>
        /// Try parse int number - return null if not int token
        /// </summary>
        private static BsonExpression TryParseInt(Tokenizer tokenizer, BsonDocument parameters)
        {
            string value = null;

            if (tokenizer.Current.Type == TokenType.Int)
            {
                value = tokenizer.Current.Value;
            }
            else if (tokenizer.Current.Type == TokenType.Minus)
            {
                var ahead = tokenizer.LookAhead(false);

                if (ahead.Type == TokenType.Int)
                {
                    value = "-" + tokenizer.ReadToken().Value;
                }
            }

            if (value != null)
            {
                var literal = JsonReader.ParseInteger(value);
                var expression = Constant(literal, parameters);
                // The lexeme is the only spelling of a Double-sized integer that reparses to the same value.
                if (literal.IsDouble) expression.Source = value;
                return expression;
            }

            return null;
        }

        /// <summary>
        /// Try parse bool - return null if not bool token
        /// </summary>
        private static BsonExpression TryParseBool(Tokenizer tokenizer, BsonDocument parameters)
        {
            if (tokenizer.Current.Type == TokenType.Word && (tokenizer.Current.Is("true") || tokenizer.Current.Is("false")))
            {
                var boolean = Convert.ToBoolean(tokenizer.Current.Value);

                return Constant(new BsonValue(boolean), parameters);
            }

            return null;
        }

        /// <summary>
        /// Try parse null constant - return null if not null token
        /// </summary>
        private static BsonExpression TryParseNull(Tokenizer tokenizer, BsonDocument parameters)
        {
            if (tokenizer.Current.Type == TokenType.Word && tokenizer.Current.Is("null"))
            {

                return Constant(BsonValue.Null, parameters);
            }

            return null;
        }

        /// <summary>
        /// Try parse string with both single/double quote - return null if not string
        /// </summary>
        private static BsonExpression TryParseString(Tokenizer tokenizer, BsonDocument parameters)
        {
            if (tokenizer.Current.Type == TokenType.String)
            {
                var bstr = new BsonValue(tokenizer.Current.Value);

                return Constant(bstr, parameters);
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Try parse json document - return null if not document token
        /// </summary>
        private static BsonExpression TryParseDocument(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.OpenBrace) return null;
            var members = new List<KeyValuePair<string, BsonExpression>>();
            if (tokenizer.LookAhead().Type == TokenType.CloseBrace)
            {
                tokenizer.ReadToken();
            }
            else
            {
                while (!tokenizer.CheckEOF())
                {
                    var key = ReadKey(tokenizer);
                    tokenizer.ReadToken();
                    BsonExpression value;
                    if (tokenizer.Current.Type == TokenType.Colon)
                    {
                        value = ParseFullExpression(tokenizer, context, parameters, scope);
                        tokenizer.ReadToken();
                    }
                    else
                    {
                        value = Path(key, true, scope, context, parameters);
                    }
                    members.Add(new KeyValuePair<string, BsonExpression>(key, value));
                    tokenizer.Current.Expect(TokenType.Comma, TokenType.CloseBrace);
                    if (tokenizer.Current.Type != TokenType.Comma) break;
                }
            }
            return Document(members, parameters);
        }


        /// <summary>
        /// Try parse source documents (when passed) * - return null if not source token
        /// </summary>
        private static BsonExpression TryParseSource(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.Asterisk) return null;

            var sourceExpr = Source(context, parameters);

            // checks if next token is "." to shortcut from "*.Name" as "MAP(*, @.Name)"
            if (tokenizer.LookAhead(false).Type == TokenType.Period)
            {
                tokenizer.ReadToken(); // consume .

                var pathExpr = BsonExpression.ParseAndCompile(tokenizer, BsonExpressionParserMode.Single, parameters, DocumentScope.Source);

                if (pathExpr == null) throw LiteException.UnexpectedToken(tokenizer.Current);

                return MapPath(sourceExpr, pathExpr, context);
            }
            else
            {
                return sourceExpr;
            }
        }

        /// <summary>
        /// Try parse array - return null if not array token
        /// </summary>
        private static BsonExpression TryParseArray(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.OpenBracket) return null;
            var values = new List<BsonExpression>();
            if (tokenizer.LookAhead().Type == TokenType.CloseBracket)
            {
                tokenizer.ReadToken();
            }
            else
            {
                while (!tokenizer.CheckEOF())
                {
                    values.Add(ParseFullExpression(tokenizer, context, parameters, scope));
                    var next = tokenizer.ReadToken().Expect(TokenType.Comma, TokenType.CloseBracket);
                    if (next.Type != TokenType.Comma) break;
                }
            }
            return Array(values, parameters);
        }


        /// <summary>
        /// Try parse parameter - return null if not parameter token
        /// </summary>
        private static BsonExpression TryParseParameter(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.At) return null;

            var ahead = tokenizer.LookAhead(false);

            if (ahead.Type == TokenType.Word || ahead.Type == TokenType.Int)
            {
                var parameterName = tokenizer.ReadToken(false).Value;
                return Parameter(parameterName, context, parameters);
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Try parse inner expression - return null if not bracket token
        /// </summary>
        private static BsonExpression TryParseInnerExpression(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.OpenParenthesis) return null;

            // read a inner expression inside ( and )
            var inner = ParseFullExpression(tokenizer, context, parameters, scope);

            // read close )
            tokenizer.ReadToken().Expect(TokenType.CloseParenthesis);

            return Group(inner);
        }

        /// <summary>
        /// Try parse method call - return null if not method call
        /// </summary>
        private static BsonExpression TryParseMethodCall(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            var token = tokenizer.Current;

            if (tokenizer.Current.Type != TokenType.Word) return null;
            if (tokenizer.LookAhead().Type != TokenType.OpenParenthesis) return null;

            // read (
            tokenizer.ReadToken();

            // get static method from this class
            var pars = new List<BsonExpression>();

            // method call with no parameters
            if (tokenizer.LookAhead().Type == TokenType.CloseParenthesis)
            {
                tokenizer.ReadToken(); // read )
            }
            else
            {
                while (!tokenizer.CheckEOF())
                {
                    var parameter = ParseFullExpression(tokenizer, context, parameters, scope);

                    pars.Add(parameter);

                    // read , or )
                    var next = tokenizer.ReadToken()
                        .Expect(TokenType.Comma, TokenType.CloseParenthesis);

                    if (next.Type == TokenType.Comma) continue;
                    break;
                }
            }

            var method = BsonExpression.GetMethod(token.Value, pars.Count);

            if (method == null) throw LiteException.UnexpectedToken($"Method '{token.Value.ToUpperInvariant()}' does not exist or contains invalid parameters", token);

            return Call(token.Value, pars, context, parameters);
        }

        /// <summary>
        /// Parse JSON-Path - return null if not method call
        /// </summary>
        private static BsonExpression TryParsePath(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.At && tokenizer.Current.Type != TokenType.Dollar && tokenizer.Current.Type != TokenType.Word) return null;
            var root = scope == DocumentScope.Root;
            if (tokenizer.Current.Type == TokenType.At || tokenizer.Current.Type == TokenType.Dollar)
            {
                root = tokenizer.Current.Type == TokenType.Dollar;
                if (tokenizer.LookAhead(false).Type == TokenType.Period)
                {
                    tokenizer.ReadToken();
                    tokenizer.ReadToken();
                }
            }
            var path = Path(ReadField(tokenizer), root, scope, context, parameters);
            while (!tokenizer.EOF)
            {
                var ahead = tokenizer.LookAhead(false);
                if (ahead.Type == TokenType.Period)
                {
                    tokenizer.ReadToken();
                    tokenizer.ReadToken(false);
                    path = Member(path, ReadField(tokenizer), scope, context);
                }
                else if (ahead.Type == TokenType.OpenBracket)
                {
                    tokenizer.ReadToken();
                    ahead = tokenizer.LookAhead();
                    if (ahead.Type == TokenType.Int || ahead.Type == TokenType.Minus)
                    {
                        var negative = ahead.Type == TokenType.Minus;
                        if (negative) tokenizer.ReadToken();
                        var digits = tokenizer.ReadToken().Expect(TokenType.Int).Value;
                        var index = Convert.ToInt32(digits);
                        path = Index(path, negative ? -index : index, null, context, (negative ? "-" : "") + digits);
                    }
                    else if (ahead.Type == TokenType.Asterisk)
                    {
                        tokenizer.ReadToken();
                        path = FilterPath(path, null, context);
                    }
                    else
                    {
                        var inner = BsonExpression.ParseAndCompile(tokenizer, BsonExpressionParserMode.Full, parameters, DocumentScope.Current);
                        path = inner.Type == BsonExpressionType.Parameter ? Index(path, 0, inner, context) : FilterPath(path, inner, context);
                    }
                    tokenizer.ReadToken().Expect(TokenType.CloseBracket);
                    if (!path.IsScalar) break;
                }
                else break;
            }
            if (!path.IsScalar && tokenizer.LookAhead(false).Type == TokenType.Period)
            {
                tokenizer.ReadToken();
                var selector = BsonExpression.ParseAndCompile(tokenizer, BsonExpressionParserMode.Single, parameters, DocumentScope.Current);
                return MapPath(path, selector, context);
            }
            return path;
        }

        /// <summary>
        /// Try parse FUNCTION methods: MAP, FILTER, SORT, ...
        /// </summary>
        private static BsonExpression TryParseFunction(Tokenizer tokenizer, ExpressionContext context, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer.Current.Type != TokenType.Word) return null;
            if (tokenizer.LookAhead().Type != TokenType.OpenParenthesis) return null;

            var token = tokenizer.Current.Value.ToUpperInvariant();

            switch (token)
            {
                case "MAP": return ParseFunction(token, BsonExpressionType.Map, tokenizer, context, parameters, scope);
                case "FILTER": return ParseFunction(token, BsonExpressionType.Filter, tokenizer, context, parameters, scope);
                case "SORT": return ParseFunction(token, BsonExpressionType.Sort, tokenizer, context, parameters, scope);
                case "VECTOR_SIM":
                    return ParseFunction(token, BsonExpressionType.VectorSim, tokenizer, context, parameters, scope,
                        convertScalarLeftToEnumerable: false, isScalarResult: true);
            }

            return null;
        }

        /// <summary>
        /// Create an array expression with 2 values (used only in BETWEEN statement)
        /// </summary>
        private static BsonExpression NewArray(BsonExpression item0, BsonExpression item1)
        {
            var values = new Expression[] { item0.Expression, item1.Expression };

            // both values must be scalar expressions
            if (item0.IsScalar == false) throw new LiteException(0, $"Expression `{item0.Source}` must be a scalar expression");
            if (item1.IsScalar == false) throw new LiteException(0, $"Expression `{item0.Source}` must be a scalar expression");

            var arrValues = Expression.NewArrayInit(typeof(BsonValue), values.ToArray());

            return new BsonExpression
            {
                Type = BsonExpressionType.Array,
                Parameters = item0.Parameters, // should be == item1.Parameters
                IsImmutable = item0.IsImmutable && item1.IsImmutable, IsVolatile = item0.IsVolatile || item1.IsVolatile,
                UseSource = item0.UseSource || item1.UseSource,
                IsScalar = true,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(item0.Fields).AddRange(item1.Fields),
                Expression = Expression.Call(_arrayInitMethod, new Expression[] { arrValues }),
                Source = item0.Source + " AND " + item1.Source
            };
        }

        /// <summary>
        /// Get field from simple \w regex or ['comp-lex'] - also, add into source. Can read empty field (root)
        /// </summary>
        private static string ReadField(Tokenizer tokenizer)
        {
            if (tokenizer.Current.Type == TokenType.OpenBracket)
            {
                var field = tokenizer.ReadToken().Expect(TokenType.String).Value;
                tokenizer.ReadToken().Expect(TokenType.CloseBracket);
                return field;
            }
            return tokenizer.Current.Type == TokenType.Word ? tokenizer.Current.Value : "";
        }

        /// <summary>
        /// Read key in document definition with single word or "comp-lex"
        /// </summary>
        public static string ReadKey(Tokenizer tokenizer)
        {
            var token = tokenizer.ReadToken();
            return token.Type == TokenType.String ? token.Value : token.Expect(TokenType.Word, TokenType.Int).Value;
        }

        public static string ReadKey(Tokenizer tokenizer, StringBuilder source)
        {
            var key = ReadKey(tokenizer);
            if (key.IsWord())
            {
                source.Append(key);
            }
            else
            {
                JsonSerializer.Serialize(key, source);
            }

            return key;
        }

        /// <summary>
        /// Read next token as Operant with ANY|ALL keyword before - returns null if next token are not an operant
        /// </summary>
        private static string ReadOperant(Tokenizer tokenizer)
        {
            var token = tokenizer.LookAhead(true);

            if (token.IsOperand)
            {
                tokenizer.ReadToken(); // consume operant

                return token.Value;
            }

            if (token.Is("ALL") || token.Is("ANY"))
            {
                var key = token.Value.ToUpperInvariant();

                tokenizer.ReadToken(); // consume operant

                token = tokenizer.ReadToken();

                if (token.IsOperand == false) throw LiteException.UnexpectedToken("Expected valid operand", token);

                return key + " " + token.Value;
            }

            return null;
        }

    }
}
