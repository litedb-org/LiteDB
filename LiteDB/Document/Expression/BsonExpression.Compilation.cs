using System;
using System.Linq.Expressions;

namespace LiteDB
{
    public sealed partial class BsonExpression
    {
#if TESTING
        [ThreadStatic]
        internal static bool DisableCompilationCache;
        private static bool CacheEnabled => !DisableCompilationCache;
#else
        private static bool CacheEnabled => true;
#endif
        private static BsonExpression CreateRoot()
        {
            var context = new ExpressionContext();
            var expression = BsonExpressionFactory.Path("", true, DocumentScope.Root, context, new BsonDocument());
            Compile(expression, context);
            return expression;
        }

        #region Static method

        private static readonly CompiledExpressionCache _compiledCache = new CompiledExpressionCache(1000);
        private static readonly ParsedExpressionCache _parsedCache = new ParsedExpressionCache();

        internal static int CompiledExpressionCount => _compiledCache.Count;

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression)
        {
            return Create(expression, new BsonDocument());
        }

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression, params BsonValue[] args)
        {
            var parameters = new BsonDocument();

            for(var i = 0; i < args.Length; i++)
            {
                parameters[i.ToString()] = args[i];
            }

            return Create(expression, parameters);
        }

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression, BsonDocument parameters)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            var eligible = CacheEnabled && expression.Length <= ParsedExpressionCache.MaximumExpressionLength;
            var repeated = false;
            if (eligible && _parsedCache.TryGet(expression, out var template, out repeated))
            {
                return parameters == null ? template.WithoutParameters() : template.Bind(parameters);
            }

            var tokenizer = new Tokenizer(expression);

            var expr = Create(tokenizer, BsonExpressionParserMode.Full, parameters);

            tokenizer.LookAhead().Expect(TokenType.EOF);

            if (eligible) _parsedCache.Add(expression, expr, repeated);

            return expr;
        }

        /// <summary>
        /// Parse tokenizer and create new instance of BsonExpression - for now, do not use cache
        /// </summary>
        internal static BsonExpression Create(Tokenizer tokenizer, BsonExpressionParserMode mode, BsonDocument parameters)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            return ParseAndCompile(tokenizer, mode, parameters, DocumentScope.Root);
        }

        /// <summary>
        /// Parse and compile string expression and return BsonExpression
        /// </summary>
        internal static BsonExpression ParseAndCompile(Tokenizer tokenizer, BsonExpressionParserMode mode, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            var context = new ExpressionContext();

            var expr =
                mode == BsonExpressionParserMode.Full ? BsonExpressionParser.ParseFullExpression(tokenizer, context, parameters, scope) :
                mode == BsonExpressionParserMode.Single ? BsonExpressionParser.ParseSingleExpression(tokenizer, context, parameters, scope) :
                mode == BsonExpressionParserMode.SelectDocument ? BsonExpressionParser.ParseSelectDocumentBuilder(tokenizer, context, parameters) :
                BsonExpressionParser.ParseUpdateDocumentBuilder(tokenizer, context, parameters);

            // Retain original parameter nodes for lazy SQL alias compilation.
            expr.SelectContext = context;

            // compile linq expression (with left+right expressions)
            Compile(expr, context);

            return expr;
        }

        internal static void Compile(BsonExpression expr, ExpressionContext context)
        {
            // Nested path/filter expressions are parsed and compiled with
            // their own ExpressionContext before being embedded in the outer
            // expression. A concurrent cap rollover can clear their cache
            // entry before the outer recursive walk reaches them; recompiling
            // such an expression against the outer context produces an
            // invalid lambda. Its instance delegate is already complete and
            // remains valid independently of cache eviction.
            if (expr.IsScalar ? expr._funcScalar != null : expr._funcEnumerable != null)
            {
                return;
            }

            // compile linq expression according with return type (scalar or enumerable)
            // in both case, try use cached compiled version
            if (expr.IsScalar)
            {
                var cached = CacheEnabled ? _compiledCache.Get<BsonExpressionScalarDelegate>(expr.Source) : null;
                if (cached == null)
                {
                    var lambda = System.Linq.Expressions.Expression.Lambda<BsonExpressionScalarDelegate>(expr.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters);
                    cached = CacheEnabled ? CompileScalarWhenNeeded(expr, lambda) : lambda.Compile();
                }

                expr._funcScalar = cached;
            }
            else
            {
                var cached = CacheEnabled ? _compiledCache.Get<BsonExpressionEnumerableDelegate>(expr.Source) : null;
                if (cached == null)
                {
                    var lambda = System.Linq.Expressions.Expression.Lambda<BsonExpressionEnumerableDelegate>(expr.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters);
                    cached = lambda.Compile();
                    if (CacheEnabled) _compiledCache.Add(expr.Source, cached);
                }

                expr._funcEnumerable = cached;
            }

            // compile child expressions (left/right)
            if (expr.Left != null) Compile(expr.Left, context);
            if (expr.Right != null) Compile(expr.Right, context);
        }

        /// <summary>
        /// Set same parameter referente to all expression child (left, right)
        /// </summary>
        internal static void SetParameters(BsonExpression expr, BsonDocument parameters)
        {
            expr.Parameters = parameters;

            if (expr.Left != null) SetParameters(expr.Left, parameters);
            if (expr.Right != null) SetParameters(expr.Right, parameters);
        }

        /// <summary>
        /// Get root document $ expression
        /// </summary>
        public static BsonExpression Root = CreateRoot();

        #endregion

    }
}
