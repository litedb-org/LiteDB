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

namespace LiteDB
{
    /// <summary>
    /// Delegate function to get compiled enumerable expression
    /// </summary>
    internal delegate IEnumerable<BsonValue> BsonExpressionEnumerableDelegate(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation, BsonDocument parameters);

    /// <summary>
    /// Delegate function to get compiled scalar expression
    /// </summary>
    internal delegate BsonValue BsonExpressionScalarDelegate(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation, BsonDocument parameters);

    /// <summary>
    /// Represents a compiled expression that can be executed against BSON documents for querying, filtering, transforming, and indexing data.
    /// </summary>
    /// <remarks>
    /// <see cref="BsonExpression"/> provides a powerful query language for LiteDB, supporting document transformation, filtering,
    /// index creation, and updates. Expressions are parsed from strings, compiled to LINQ expressions, and cached for performance.
    /// For detailed syntax and examples, see: https://github.com/mbdavid/LiteDB/wiki/Expressions
    /// </remarks>
    public sealed class BsonExpression
    {
        /// <summary>
        /// Gets the formatted string representation of the expression as it was parsed.
        /// </summary>
        public string Source { get; internal set; }

        /// <summary>
        /// Gets the type of expression, indicating its operation or purpose.
        /// </summary>
        public BsonExpressionType Type { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this expression produces the same result for the same input document and parameters.
        /// </summary>
        /// <remarks>
        /// Immutable expressions can be cached and optimized more aggressively. Non-immutable expressions include those using
        /// functions like NOW() or expressions that depend on parameters that may change between evaluations.
        /// </remarks>
        public bool IsImmutable { get; internal set; }

        /// <summary>
        /// Gets or sets the parameter values that will be used during expression execution.
        /// </summary>
        public BsonDocument Parameters { get; internal set; }

        /// <summary>
        /// Gets the left-hand side expression in binary predicate expressions (e.g., equals, greater than).
        /// </summary>
        internal BsonExpression Left { get; set; }

        /// <summary>
        /// Gets the right-hand side expression in binary predicate expressions (e.g., equals, greater than).
        /// </summary>
        internal BsonExpression Right { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this expression or any nested expression references the global source using the wildcard (<c>*</c>).
        /// </summary>
        internal bool UseSource { get; set; }

        /// <summary>
        /// Gets the compiled LINQ expression tree representation of this BSON expression.
        /// </summary>
        internal Expression Expression { get; set; }

        /// <summary>
        /// Gets the set of field names used at the root level of the document for partial deserialization optimization.
        /// </summary>
        /// <remarks>
        /// The special value <c>$</c> indicates all fields are required. This set is used to optimize document loading
        /// by only deserializing the fields actually needed by the expression.
        /// </remarks>
        public HashSet<string> Fields { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this expression returns a single value (<see langword="true"/>) or an enumerable collection (<see langword="false"/>).
        /// </summary>
        public bool IsScalar { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this expression is a predicate that evaluates to <see langword="true"/> or <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// Predicate expressions include comparison operators (=, &gt;, &lt;, etc.) but exclude logical operators (AND, OR).
        /// Predicate expressions always have both <see cref="Left"/> and <see cref="Right"/> sub-expressions.
        /// </remarks>
        internal bool IsPredicate =>
            this.Type == BsonExpressionType.Equal ||
            this.Type == BsonExpressionType.Like ||
            this.Type == BsonExpressionType.Between ||
            this.Type == BsonExpressionType.GreaterThan ||
            this.Type == BsonExpressionType.GreaterThanOrEqual ||
            this.Type == BsonExpressionType.LessThan ||
            this.Type == BsonExpressionType.LessThanOrEqual ||
            this.Type == BsonExpressionType.NotEqual ||
            this.Type == BsonExpressionType.In;

        /// <summary>
        /// Gets a value indicating whether this expression can be used for creating an index.
        /// </summary>
        /// <remarks>
        /// An expression is indexable when it references at least one field, contains only immutable methods, and has no parameters.
        /// </remarks>
        internal bool IsIndexable =>
            this.Fields.Count > 0 &&
            this.IsImmutable == true &&
            this.Parameters.Count == 0;

        /// <summary>
        /// Gets a value indicating whether this expression has no document dependencies and can be used as a constant value.
        /// </summary>
        /// <remarks>
        /// Value expressions contain no field references and can be evaluated independently of any document.
        /// </remarks>
        internal bool IsValue =>
            this.Fields.Count == 0;

        /// <summary>
        /// Gets a value indicating whether this predicate expression uses the ANY keyword for filtering array items.
        /// </summary>
        internal bool IsANY =>
            this.IsPredicate &&
            this.Expression.ToString().Contains("_ANY");

        /// <summary>
        /// The compiled delegate for enumerable expressions that return multiple values.
        /// </summary>
        private BsonExpressionEnumerableDelegate _funcEnumerable;

        /// <summary>
        /// Compiled Expression into a scalar function to be executed: func(source[], root, current, parameters)1
        /// </summary>
        private BsonExpressionScalarDelegate _funcScalar;

        /// <summary>
        /// Get default field name when need convert simple BsonValue into BsonDocument
        /// </summary>
        internal string DefaultFieldName()
        {
            var name = string.Join("_", this.Fields.Where(x => x != "$"));

            return string.IsNullOrEmpty(name) ? "expr" : name;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonExpression"/> class. Only used internally by the parser.
        /// </summary>
        internal BsonExpression()
        {
        }

        /// <summary>
        /// Implicitly converts a <see cref="BsonExpression"/> to its string representation.
        /// </summary>
        /// <param name="expr">The expression to convert.</param>
        public static implicit operator String(BsonExpression expr)
        {
            return expr.Source;
        }

        /// <summary>
        /// Implicitly converts a string to a <see cref="BsonExpression"/> by parsing it.
        /// </summary>
        /// <param name="expr">The expression string to parse.</param>
        public static implicit operator BsonExpression(String expr)
        {
            return BsonExpression.Create(expr);
        }

        #region Execute Enumerable

        /// <summary>
        /// Executes the expression with an empty document context, used for evaluating math expressions and functions without document dependencies.
        /// </summary>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>An enumerable collection of <see cref="BsonValue"/> results.</returns>
        public IEnumerable<BsonValue> Execute(Collation collation = null)
        {
            var root = new BsonDocument();
            var source = new BsonDocument[] { root };

            return this.Execute(source, root, root, collation);
        }

        /// <summary>
        /// Executes the expression against a single document and returns an enumerable collection of results.
        /// </summary>
        /// <param name="root">The document to evaluate the expression against.</param>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>An enumerable collection of <see cref="BsonValue"/> results.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="root"/> is <see langword="null"/>.</exception>
        public IEnumerable<BsonValue> Execute(BsonDocument root, Collation collation = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var source = new BsonDocument[] { root };

            return this.Execute(source, root, root, collation);
        }

        /// <summary>
        /// Executes the expression against a collection of documents and returns an enumerable collection of results.
        /// </summary>
        /// <param name="source">The collection of documents to evaluate the expression against.</param>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>An enumerable collection of <see cref="BsonValue"/> results.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is <see langword="null"/>.</exception>
        public IEnumerable<BsonValue> Execute(IEnumerable<BsonDocument> source, Collation collation = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            return this.Execute(source, null, null, collation);
        }

        /// <summary>
        /// Executes the expression with full context and returns an enumerable collection of results.
        /// </summary>
        /// <param name="source">The source collection of documents.</param>
        /// <param name="root">The root document context.</param>
        /// <param name="current">The current value being processed.</param>
        /// <param name="collation">The collation to use for string comparisons.</param>
        /// <returns>An enumerable collection of <see cref="BsonValue"/> results.</returns>
        internal IEnumerable<BsonValue> Execute(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation)
        {
            if (this.IsScalar)
            {
                var value = _funcScalar(source, root, current, collation ?? Collation.Binary, this.Parameters);

                yield return value;
            }
            else
            {
                var values = _funcEnumerable(source, root, current, collation ?? Collation.Binary, this.Parameters);

                foreach (var value in values)
                {
                    yield return value;
                }
            }
        }

        /// <summary>
        /// Executes the expression against a document to extract all unique index keys.
        /// </summary>
        /// <param name="doc">The document to extract index keys from.</param>
        /// <param name="collation">The collation to use for string comparisons.</param>
        /// <returns>A distinct collection of <see cref="BsonValue"/> index keys (no duplicate keys for the same document).</returns>
        internal IEnumerable<BsonValue> GetIndexKeys(BsonDocument doc, Collation collation)
        {
            return this.Execute(doc, collation).Distinct();
        }

        #endregion

        #region ExecuteScalar

        /// <summary>
        /// Executes a scalar expression with an empty document context, used for evaluating math expressions and functions without document dependencies.
        /// </summary>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>A single <see cref="BsonValue"/> result, or <see cref="BsonValue.Null"/> if the expression produces no result.</returns>
        public BsonValue ExecuteScalar(Collation collation = null)
        {
            var root = new BsonDocument();
            var source = new BsonDocument[] { };

            return this.ExecuteScalar(source, root, root, collation);
        }

        /// <summary>
        /// Executes a scalar expression against a single document and returns a single value.
        /// </summary>
        /// <param name="root">The document to evaluate the expression against.</param>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>A single <see cref="BsonValue"/> result, or <see cref="BsonValue.Null"/> if the expression produces no result.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="root"/> is <see langword="null"/>.</exception>
        /// <exception cref="LiteException">Thrown when the expression is not scalar and can return multiple results.</exception>
        public BsonValue ExecuteScalar(BsonDocument root, Collation collation = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var source = new BsonDocument[] { root };

            return this.ExecuteScalar(source, root, root, collation);
        }

        /// <summary>
        /// Executes a scalar expression against a collection of documents and returns a single value.
        /// </summary>
        /// <param name="source">The collection of documents to evaluate the expression against.</param>
        /// <param name="collation">The collation to use for string comparisons. If <see langword="null"/>, uses <see cref="Collation.Binary"/>.</param>
        /// <returns>A single <see cref="BsonValue"/> result, or <see cref="BsonValue.Null"/> if the expression produces no result.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is <see langword="null"/>.</exception>
        /// <exception cref="LiteException">Thrown when the expression is not scalar and can return multiple results.</exception>
        public BsonValue ExecuteScalar(IEnumerable<BsonDocument> source, Collation collation = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            return this.ExecuteScalar(source, null, null, collation);
        }

        /// <summary>
        /// Executes a scalar expression with full context and returns a single value.
        /// </summary>
        /// <param name="source">The source collection of documents.</param>
        /// <param name="root">The root document context.</param>
        /// <param name="current">The current value being processed.</param>
        /// <param name="collation">The collation to use for string comparisons.</param>
        /// <returns>A single <see cref="BsonValue"/> result.</returns>
        /// <exception cref="LiteException">Thrown when the expression is not scalar and can return multiple results.</exception>
        internal BsonValue ExecuteScalar(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation)
        {
            if (this.IsScalar)
            {
                return _funcScalar(source, root, current, collation ?? Collation.Binary, this.Parameters);
            }
            else
            {
                throw new LiteException(0, $"Expression `{this.Source}` is not a scalar expression and can return more than one result");
            }
        }

        #endregion

        #region Static method

        private static readonly ConcurrentDictionary<string, BsonExpressionEnumerableDelegate> _cacheEnumerable = new ConcurrentDictionary<string, BsonExpressionEnumerableDelegate>();
        private static readonly ConcurrentDictionary<string, BsonExpressionScalarDelegate> _cacheScalar = new ConcurrentDictionary<string, BsonExpressionScalarDelegate>();

        /// <summary>
        /// Parses a string expression and creates a new <see cref="BsonExpression"/> instance with no parameters.
        /// </summary>
        /// <param name="expression">The expression string to parse.</param>
        /// <returns>A compiled <see cref="BsonExpression"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <see langword="null"/> or whitespace.</exception>
        /// <remarks>
        /// Compiled expressions are cached for performance. Subsequent calls with the same expression string will return
        /// a cached compiled version.
        /// </remarks>
        public static BsonExpression Create(string expression)
        {
            return Create(expression, new BsonDocument());
        }

        /// <summary>
        /// Parses a string expression and creates a new <see cref="BsonExpression"/> instance with positional parameters.
        /// </summary>
        /// <param name="expression">The expression string to parse, using <c>@0</c>, <c>@1</c>, etc. for parameter placeholders.</param>
        /// <param name="args">The parameter values to substitute into the expression.</param>
        /// <returns>A compiled <see cref="BsonExpression"/> instance with parameters set.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <see langword="null"/> or whitespace.</exception>
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
        /// Parses a string expression and creates a new <see cref="BsonExpression"/> instance with named parameters.
        /// </summary>
        /// <param name="expression">The expression string to parse.</param>
        /// <param name="parameters">A document containing named parameter values to use in the expression.</param>
        /// <returns>A compiled <see cref="BsonExpression"/> instance with parameters set.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <see langword="null"/> or whitespace.</exception>
        /// <remarks>
        /// Compiled expressions are cached for performance based on the expression string.
        /// </remarks>
        public static BsonExpression Create(string expression, BsonDocument parameters)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            var tokenizer = new Tokenizer(expression);

            var expr = Create(tokenizer, BsonExpressionParserMode.Full, parameters);

            tokenizer.LookAhead().Expect(TokenType.EOF);

            return expr;
        }

        /// <summary>
        /// Parses a tokenizer stream and creates a new <see cref="BsonExpression"/> instance.
        /// </summary>
        /// <param name="tokenizer">The tokenizer containing the expression tokens to parse.</param>
        /// <param name="mode">The parsing mode that determines how the expression is interpreted.</param>
        /// <param name="parameters">A document containing parameter values to use in the expression.</param>
        /// <returns>A compiled <see cref="BsonExpression"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="tokenizer"/> is <see langword="null"/>.</exception>
        internal static BsonExpression Create(Tokenizer tokenizer, BsonExpressionParserMode mode, BsonDocument parameters)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            return ParseAndCompile(tokenizer, mode, parameters, DocumentScope.Root);
        }

        /// <summary>
        /// Parses and compiles a tokenized expression into a <see cref="BsonExpression"/> instance.
        /// </summary>
        /// <param name="tokenizer">The tokenizer containing the expression tokens.</param>
        /// <param name="mode">The parsing mode that determines how the expression is interpreted.</param>
        /// <param name="parameters">Parameter values to use in the expression.</param>
        /// <param name="scope">The document scope context for parsing nested expressions.</param>
        /// <returns>A compiled <see cref="BsonExpression"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="tokenizer"/> is <see langword="null"/>.</exception>
        internal static BsonExpression ParseAndCompile(Tokenizer tokenizer, BsonExpressionParserMode mode, BsonDocument parameters, DocumentScope scope)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            var context = new ExpressionContext();

            var expr =
                mode == BsonExpressionParserMode.Full ? BsonExpressionParser.ParseFullExpression(tokenizer, context, parameters, scope) :
                mode == BsonExpressionParserMode.Single ? BsonExpressionParser.ParseSingleExpression(tokenizer, context, parameters, scope) :
                mode == BsonExpressionParserMode.SelectDocument ? BsonExpressionParser.ParseSelectDocumentBuilder(tokenizer, context, parameters) :
                BsonExpressionParser.ParseUpdateDocumentBuilder(tokenizer, context, parameters);

            // compile linq expression (with left+right expressions)
            Compile(expr, context);

            return expr;
        }

        /// <summary>
        /// Compiles a <see cref="BsonExpression"/> and its child expressions into cached delegate functions.
        /// </summary>
        /// <param name="expr">The expression to compile.</param>
        /// <param name="context">The expression context containing parameter definitions.</param>
        internal static void Compile(BsonExpression expr, ExpressionContext context)
        {
            // compile linq expression according with return type (scalar or enumerable)
            // in both case, try use cached compiled version
            if (expr.IsScalar)
            {
                var cached = _cacheScalar.GetOrAdd(expr.Source, s =>
                {
                    var lambda = System.Linq.Expressions.Expression.Lambda<BsonExpressionScalarDelegate>(expr.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters);

                    return lambda.Compile();
                });

                expr._funcScalar = cached;
            }
            else
            {
                var cached = _cacheEnumerable.GetOrAdd(expr.Source, s =>
                {
                    var lambda = System.Linq.Expressions.Expression.Lambda<BsonExpressionEnumerableDelegate>(expr.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters);

                    return lambda.Compile();
                });

                expr._funcEnumerable = cached;
            }

            // compile child expressions (left/right)
            if (expr.Left != null) Compile(expr.Left, context);
            if (expr.Right != null) Compile(expr.Right, context);
        }

        /// <summary>
        /// Sets the same parameter document reference on the expression and all its child expressions (left, right).
        /// </summary>
        /// <param name="expr">The expression to update.</param>
        /// <param name="parameters">The parameters document to set.</param>
        internal static void SetParameters(BsonExpression expr, BsonDocument parameters)
        {
            expr.Parameters = parameters;

            if (expr.Left != null) SetParameters(expr.Left, parameters);
            if (expr.Right != null) SetParameters(expr.Right, parameters);
        }

        /// <summary>
        /// Gets a predefined expression that references the root document using the <c>$</c> symbol.
        /// </summary>
        public static BsonExpression Root = Create("$");

        #endregion

        #region MethodCall quick access

        /// <summary>
        /// Gets all registered methods available for use in BSON expressions.
        /// </summary>
        public static IEnumerable<MethodInfo> Methods => _methods.Values;

        /// <summary>
        /// Dictionary containing all static methods from <see cref="BsonExpressionMethods"/>, indexed by name and parameter count.
        /// </summary>
        private static readonly Dictionary<string, MethodInfo> _methods =
            typeof(BsonExpressionMethods).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(m => m.Name.ToUpperInvariant() + "~" + m.GetParameters().Where(p => p.ParameterType != typeof(Collation)).Count());

        /// <summary>
        /// Gets an expression method with the specified name and parameter count.
        /// </summary>
        /// <param name="name">The method name (case-insensitive).</param>
        /// <param name="parameterCount">The number of parameters the method accepts (excluding collation parameter).</param>
        /// <returns>The <see cref="MethodInfo"/> for the matching method, or <see langword="null"/> if not found.</returns>
        internal static MethodInfo GetMethod(string name, int parameterCount)
        {
            var key = name.ToUpperInvariant() + "~" + parameterCount;

            return _methods.GetOrDefault(key);
        }

        #endregion

        #region FunctionCall quick access

        /// <summary>
        /// Gets all registered functions available for use in BSON expressions.
        /// </summary>
        public static IEnumerable<MethodInfo> Functions => _functions.Values;

        /// <summary>
        /// Dictionary containing all static functions from <see cref="BsonExpressionFunctions"/>, indexed by name and parameter count.
        /// </summary>
        private static readonly Dictionary<string, MethodInfo> _functions =
            typeof(BsonExpressionFunctions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(m => m.Name.ToUpperInvariant() + "~" + m.GetParameters()
            .Skip(5).Count());

        /// <summary>
        /// Gets an expression function with the specified name and parameter count.
        /// </summary>
        /// <param name="name">The function name (case-insensitive).</param>
        /// <param name="parameterCount">The number of parameters the function accepts. Default is 0.</param>
        /// <returns>The <see cref="MethodInfo"/> for the matching function, or <see langword="null"/> if not found.</returns>
        internal static MethodInfo GetFunction(string name, int parameterCount = 0)
        {
            var key = name.ToUpperInvariant() + "~" + parameterCount;

            return _functions.GetOrDefault(key);
        }

        #endregion

        /// <summary>
        /// Returns a string representation of this expression including its source and type.
        /// </summary>
        /// <returns>A string in the format: `source` [type].</returns>
        public override string ToString()
        {
            return $"`{this.Source}` [{this.Type}]";
        }
    }
}