using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class LinqExpressionVisitor : ExpressionVisitor
    {
        private static readonly Dictionary<Type, ITypeResolver> _resolver = new Dictionary<Type, ITypeResolver>
        {
            [typeof(BsonValue)] = new BsonValueResolver(),
            [typeof(BsonArray)] = new BsonValueResolver(),
            [typeof(BsonDocument)] = new BsonValueResolver(),
            [typeof(Convert)] = new ConvertResolver(),
            [typeof(DateTime)] = new DateTimeResolver(),
            [typeof(Int32)] = new NumberResolver("INT32"),
            [typeof(Int64)] = new NumberResolver("INT64"),
            [typeof(Decimal)] = new NumberResolver("DECIMAL"),
            [typeof(Double)] = new NumberResolver("DOUBLE"),
            [typeof(ICollection)] = new ICollectionResolver(),
            [typeof(Enumerable)] = new EnumerableResolver(),
            [typeof(Guid)] = new GuidResolver(),
            [typeof(Math)] = new MathResolver(),
            [typeof(Regex)] = new RegexResolver(),
            [typeof(ObjectId)] = new ObjectIdResolver(),
            [typeof(String)] = new StringResolver(),
            [typeof(Nullable)] = new NullableResolver()
        };

        private readonly BsonMapper _mapper;
        private readonly Expression _expr;
        private readonly ParameterExpression _rootParameter = null;

        private readonly BsonDocument _parameters = new BsonDocument();
        private int _paramIndex = 0;
        private Type _dbRefType = null;

        private readonly StringBuilder _builder = new StringBuilder();
        private readonly Stack<Expression> _nodes = new Stack<Expression>();

        public LinqExpressionVisitor(BsonMapper mapper, Expression expr)
        {
            _mapper = mapper;
            _expr = expr;

            if (expr is LambdaExpression lambda)
            {
                _rootParameter = lambda.Parameters.First();
            }
            else
            {
                throw new NotSupportedException($"Expression {expr.ToString()} must be a lambda expression");
            }
        }

        public BsonExpression Resolve(bool predicate)
        {
            this.Visit(_expr);

            ENSURE(_nodes.Count == 0, "node stack must be empty when finish expression resolve");

            var expression = _builder.ToString();

            try
            {
                var e = BsonExpression.Create(expression, _parameters);

                // if expression must return an predicate but expression result is Path/Parameter/Call add `= true`
                if (predicate && (e.Type == BsonExpressionType.Path || e.Type == BsonExpressionType.Call || e.Type == BsonExpressionType.Parameter))
                {
                    expression = "(" + expression + " = true)";

                    e = BsonExpression.Create(expression, _parameters);
                }

                return e;
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {_expr.ToString()} - `{expression}`", ex);
            }
        }

        /// <summary>
        /// Visit :: `x => x.Customer.Name`
        /// </summary>
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            var l = base.VisitLambda(node);

            // remove last parameter $ (or @)
            _builder.Length--;

            return l;
        }

        /// <summary>
        /// Visit lambda invocation
        /// </summary>
        protected override Expression VisitInvocation(InvocationExpression node)
        {
            var l = base.VisitInvocation(node);

            // remove last parameter $ (or @)
            _builder.Length--;

            return l;
        }

        /// <summary>
        /// Visit :: x => `x`.Customer.Name
        /// </summary>
        protected override Expression VisitParameter(ParameterExpression node)
        {
            _builder.Append(_rootParameter.Equals(node) ? "$" : "@");

            return base.VisitParameter(node);
        }

        /// <summary>
        /// Visit :: x => x.`Customer.Name`
        /// </summary>
        protected override Expression VisitMember(MemberExpression node)
        {
            // test if member access is based on parameter expression or constant/external variable
            var isParam = ParameterExpressionVisitor.Test(node);

            var member = node.Member;

            // special types contains method access: string.Length, DateTime.Day, ...
            if (this.TryGetResolver(member.DeclaringType, out var type))
            {
                var pattern = type.ResolveMember(member);

                if (pattern == null) throw new NotSupportedException($"Member {member.Name} are not support in {member.DeclaringType.Name} when convert to BsonExpression ({node.ToString()}).");

                this.ResolvePattern(pattern, node.Expression, new Expression[0]);
            }
            else
            {
                // for static member, Expression == null
                if (node.Expression != null)
                {
                    _nodes.Push(node);

                    base.Visit(node.Expression);

                    if (isParam)
                    {
                        var name = this.ResolveMember(member);

                        _builder.Append(name);
                    }
                }
                // static member is not parameter expression - compile and execute as constant
                else
                {
                    var value = this.Evaluate(node);

                    base.Visit(Expression.Constant(value));
                }
            }

            if (_nodes.Count > 0)
            {
                _nodes.Pop();
            }


            return node;
        }

        /// <summary>
        /// Visit :: x => x.Customer.Name.`ToUpper()`
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // if special method for index access, eval index value (do not use parameters)
            if (this.IsMethodIndexEval(node, out var obj, out var idx))
            {
                this.Visit(obj);

                var index = this.Evaluate(idx, typeof(string), typeof(int));

                if (index is string)
                {
                    _builder.Append(".");
                    _builder.Append($"['{index}']");
                }
                else
                {
                    _builder.Append($"[{index}]");
                }

                return node;
            }

            // if not found in resolver, try run method
            if (!this.TryGetResolver(node.Method.DeclaringType, out var type))
            {
                // if method are called by parameter expression and it's not exists, throw error
                var isParam = ParameterExpressionVisitor.Test(node);

                if (isParam) throw new NotSupportedException($"Method {node.Method.Name} not available to convert to BsonExpression ({node.ToString()}).");

                // otherwise, try compile and execute
                var value = this.Evaluate(node);

                base.Visit(Expression.Constant(value));

                return node;
            }

            // otherwise I have resolver for this method
            var pattern = type.ResolveMethod(node.Method);

            if (pattern == null) throw new NotSupportedException($"Method {Reflection.MethodName(node.Method)} in {node.Method.DeclaringType.Name} are not supported when convert to BsonExpression ({node.ToString()}).");

            // run pattern using object as # and args as @n
            this.ResolvePattern(pattern, node.Object, node.Arguments);

            return node;
        }

        /// <summary>
        /// Visit :: x => x.Age + `10` (will create parameter:  `p0`, `p1`, ...)
        /// </summary>
        protected override Expression VisitConstant(ConstantExpression node)
        {
            MemberExpression prevNode;
            var value = node.Value;

            // https://stackoverflow.com/a/29708655/3286260
            while (_nodes.Count > 0 && (prevNode = _nodes.Peek() as MemberExpression) != null)
            {
                if (prevNode.Member is FieldInfo fieldInfo)
                {
                    value = fieldInfo.GetValue(value);
                }
                else if (prevNode.Member is PropertyInfo propertyInfo)
                {
                    value = propertyInfo.GetValue(value);
                }

                _nodes.Pop();
            }

            ENSURE(_nodes.Count == 0, "counter stack must be zero to eval all properties/field over object");

            var parameter = "p" + (_paramIndex++);

            _builder.AppendFormat("@" + parameter);

            var type = value?.GetType();

            // if type is string, use direct BsonValue(string) to avoid rules like TrimWhitespace/EmptyStringToNull in mapper
            var arg = type == null ? BsonValue.Null :
                type == typeof(string) ? new BsonValue((string)value) :
                _mapper.Serialize(value.GetType(), value);

            _parameters[parameter] = arg;

            return node;
        }

        /// <summary>
        /// Visit :: x => `!x.Active`
        /// </summary>
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Not)
            {
                // when is only "not boolean" resolve as 'x => !x.Active' = '$.Active = false'
                if (node.Operand.NodeType == ExpressionType.MemberAccess)
                {
                    _builder.Append("(");
                    this.Visit(node.Operand);
                    _builder.Append(" = false)");
                }
                // otherwise, resolve all expression as inner expression = false
                else
                {
                    _builder.Append("(");
                    this.Visit(node.Operand);
                    _builder.Append(")");
                    _builder.Append(" = false");
                }
            }
            else if (node.NodeType == ExpressionType.Convert)
            {
                var fromType = node.Operand.Type;
                var toType = node.Type;

                // do Numeric cast only from "Double/Decimal" to "Int32/Int64"
                if ((fromType == typeof(Double) || fromType == typeof(Decimal)) &&
                    (toType == typeof(Int32) || toType == typeof(Int64)))
                {
                    var methodName = "To" + toType.Name.ToString();

                    var convert = typeof(Convert).GetMethods()
                        .Where(x => x.Name == methodName)
                        .Where(x => x.GetParameters().Length == 1 && x.GetParameters().Any(z => z.ParameterType == fromType))
                        .FirstOrDefault();

                    if (convert == null) throw new NotSupportedException($"Cast from {fromType.Name} are not supported when convert to BsonExpression");

                    var method = Expression.Call(null, convert, node.Operand);

                    this.VisitMethodCall(method);
                }
                else
                {
                    base.VisitUnary(node);
                }
            }
            else if (node.NodeType == ExpressionType.ArrayLength)
            {
                _builder.Append("LENGTH(");
                this.Visit(node.Operand);
                _builder.Append(")");
            }
            else
            {
                base.VisitUnary(node);
            }

            return node;
        }

        /// <summary>
        /// Visit :: x => `new { x.Id, x.Name }`
        /// </summary>
        protected override Expression VisitNew(NewExpression node)
        {
            if (node.Members == null)
            {
                if (this.TryGetResolver(node.Type, out var type))
                {
                    var pattern = type.ResolveCtor(node.Constructor);

                    if (pattern == null) throw new NotSupportedException($"Constructor for {node.Type.Name} are not supported when convert to BsonExpression ({node.ToString()}).");

                    this.ResolvePattern(pattern, null, node.Arguments);
                }
                else
                {
                    throw new NotSupportedException($"New instance are not supported for {node.Type} when convert to BsonExpression ({node.ToString()}).");
                }
            }
            else
            {
                _builder.Append("{ ");

                for (var i = 0; i < node.Members.Count; i++)
                {
                    var member = node.Members[i];
                    _builder.Append(i > 0 ? ", " : "");
                    _builder.AppendFormat("'{0}': ", member.Name);
                    this.Visit(node.Arguments[i]);
                }

                _builder.Append(" }");
            }

            return node;
        }

        /// <summary>
        /// Visit :: x => `new MyClass { Id = 10 }`
        /// </summary>
        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            // works only for empty ctor
            if (node.NewExpression.Constructor.GetParameters().Length > 0)
            {
                throw new NotSupportedException($"New instance of {node.Type} are not supported because contains ctor with parameter. Try use only property initializers: `new {node.Type.Name} {{ PropA = 1, PropB == \"John\" }}`.");
            }

            _builder.Append("{");

            for (var i = 0; i < node.Bindings.Count; i++)
            {
                var bind = node.Bindings[i] as MemberAssignment;
                var member = this.ResolveMember(bind.Member);

                _builder.Append(i > 0 ? ", " : "");
                _builder.Append(member.Substring(1));
                _builder.Append(":");

                this.Visit(bind.Expression);
            }

            _builder.Append("}");

            return node;
        }

        /// <summary>
        /// Visit :: x => `new int[] { 1, 2, 3 }`
        /// </summary>
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            _builder.Append("[ ");

            for (var i = 0; i < node.Expressions.Count; i++)
            {
                _builder.Append(i > 0 ? ", " : "");
                this.Visit(node.Expressions[i]);
            }

            _builder.Append(" ]");

            return node;
        }

        /// <summary>
        /// Visit :: x => x.Id `+` 10
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            var andOr = node.NodeType == ExpressionType.AndAlso || node.NodeType == ExpressionType.OrElse;

            // special visitors
            if (node.NodeType == ExpressionType.Coalesce) return this.VisitCoalesce(node);
            if (node.NodeType == ExpressionType.ArrayIndex) return this.VisitArrayIndex(node);

            var op = this.GetOperator(node.NodeType);

            _builder.Append("(");

            this.VisitAsPredicate(node.Left, andOr);

            _builder.Append(op);

            if (!_mapper.EnumAsInteger &&
                node.Left.NodeType == ExpressionType.Convert &&
                node.Left is UnaryExpression unex &&
                unex.Operand.Type.GetTypeInfo().IsEnum &&
                unex.Type == typeof(Int32))
            {
                this.VisitAsPredicate(Expression.Constant(Enum.GetName(unex.Operand.Type, this.Evaluate(node.Right))), andOr);
            }
            else
            {
                this.VisitAsPredicate(node.Right, andOr);
            }

            _builder.Append(")");

            return node;
        }

        /// <summary>
        /// Visit :: x => `x.Id > 0 ? "ok" : "not-ok"`
        /// </summary>
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            _builder.Append("IIF(");
            this.Visit(node.Test);
            _builder.Append(", ");
            this.Visit(node.IfTrue);
            _builder.Append(", ");
            this.Visit(node.IfFalse);
            _builder.Append(")");

            return node;
        }

        /// <summary>
        /// Visit :: x => `x.FirstName ?? x.LastName`
        /// </summary>
        private Expression VisitCoalesce(BinaryExpression node)
        {
            _builder.Append("COALESCE(");
            this.Visit(node.Left);
            _builder.Append(", ");
            this.Visit(node.Right);
            _builder.Append(")");

            return node;
        }

        /// <summary>
        /// Visit :: x => `x.Items[5]`
        /// </summary>
        private Expression VisitArrayIndex(BinaryExpression node)
        {
            this.Visit(node.Left);
            _builder.Append("[");
            // index must be evaluated (must returns a constant)
            var index = this.Evaluate(node.Right, typeof(int));
            _builder.Append(index);
            _builder.Append("]");

            return node;
        }

        /// <summary>
        /// Resolve string pattern using an object + N arguments. Will write over _builder
        /// </summary>
        private void ResolvePattern(string pattern, Expression obj, IList<Expression> args)
        {
            var tokenizer = new Tokenizer(pattern);

            // lets use tokenizer to parse this method pattern
            while (!tokenizer.EOF)
            {
                var token = tokenizer.ReadToken(false);

                if (token.Type == TokenType.Hashtag)
                {
                    this.Visit(obj);
                }
                else if (token.Type == TokenType.At && tokenizer.LookAhead(false).Type == TokenType.Int)
                {
                    var i = Convert.ToInt32(tokenizer.ReadToken(false).Expect(TokenType.Int).Value);

                    this.Visit(args[i]);
                }
                else if (token.Type == TokenType.Percent)
                {
                    // special ANY/ALL cases
                    this.VisitEnumerablePredicate(args[1] as LambdaExpression);
                }
                else
                {
                    _builder.Append(token.Type == TokenType.String ? "'" + token.Value + "'" : token.Value);
                }
            }
        }

        /// <summary>
        /// Resolve Enumerable predicate when using Any/All enumerable extensions
        /// </summary>
        private void VisitEnumerablePredicate(LambdaExpression lambda)
        {
            var expression = lambda.Body;

            // Visit .Any(x => `x == 10`)
            if (expression is BinaryExpression bin)
            {
                // requires only parameter in left side
                if (bin.Left.NodeType != ExpressionType.Parameter) throw new LiteException(0, "Any/All requires simple parameter on left side. Eg: `x => x.Phones.Select(p => p.Number).Any(n => n > 5)`");

                var op = this.GetOperator(bin.NodeType);

                _builder.Append(op);

                this.VisitAsPredicate(bin.Right, false);
            }
            // Visit .Any(x => `x.StartsWith("John")`)
            else if (expression is MethodCallExpression met)
            {
                // requires only parameter in left side
                if (met.Object.NodeType != ExpressionType.Parameter) throw new NotSupportedException("Any/All requires simple parameter on left side. Eg: `x.Customers.Select(c => c.Name).Any(n => n.StartsWith('J'))`");

                // if not found in resolver, try run method
                if (!this.TryGetResolver(met.Method.DeclaringType, out var type))
                {
                    throw new NotSupportedException($"Method {met.Method.Name} not available to convert to BsonExpression inside Any/All call.");
                }

                // otherwise I have resolver for this method
                var pattern = type.ResolveMethod(met.Method);

                if (pattern == null || !pattern.StartsWith("#")) throw new NotSupportedException($"Method {met.Method.Name} not available to convert to BsonExpression inside Any/All call.");

                // call resolve pattern removing first `#`
                this.ResolvePattern(pattern.Substring(1), met.Object, met.Arguments);
            }
            else
            {
                throw new LiteException(0, "When using Any/All method test do only simple predicate variable. Eg: `x => x.Phones.Select(p => p.Number).Any(n => n > 5)`");
            }

        }

        /// <summary>
        /// Get string operator from an Binary expression
        /// </summary>
        private string GetOperator(ExpressionType nodeType)
        {
            switch (nodeType)
            {
                case ExpressionType.Add: return " + ";
                case ExpressionType.Multiply: return " * ";
                case ExpressionType.Subtract: return " - ";
                case ExpressionType.Divide: return " / ";
                case ExpressionType.Equal: return " = ";
                case ExpressionType.NotEqual: return " != ";
                case ExpressionType.GreaterThan: return " > ";
                case ExpressionType.GreaterThanOrEqual: return " >= ";
                case ExpressionType.LessThan: return " < ";
                case ExpressionType.LessThanOrEqual: return " <= ";
                case ExpressionType.And: return " AND ";
                case ExpressionType.AndAlso: return " AND ";
                case ExpressionType.Or: return " OR ";
                case ExpressionType.OrElse: return " OR ";
            }

            throw new NotSupportedException($"Operator not supported {nodeType}");
        }

        /// <summary>
        /// Returns document field name for some type member
        /// </summary>
        private string ResolveMember(MemberInfo member)
        {
            var name = member.Name;

            // checks if parent field are not DbRef (checks for same dataType)
            var isParentDbRef = _dbRefType != null && member.DeclaringType.IsAssignableFrom(_dbRefType);

            // get class entity from mapper
            var entity = _mapper.GetEntityMapper(member.DeclaringType);
            entity.WaitForInitialization();

            // get mapped field from entity
            var field = entity.Members.FirstOrDefault(x => x.MemberName == name);

            if (field == null) throw new NotSupportedException($"Member {name} not found on BsonMapper for type {member.DeclaringType}.");

            // define if this field are DbRef (child will need check parent)
            _dbRefType = field.IsDbRef ? field.UnderlyingType : null;

            // if parent call is DbRef and are calling _id field, rename to $id
            return "." + (isParentDbRef && field.FieldName == "_id" ? "$id" : field.FieldName);
        }

        /// <summary>
        /// Define if this method is index access and must eval index value (do not use parameter)
        /// </summary>
        private bool IsMethodIndexEval(MethodCallExpression node, out Expression obj, out Expression idx)
        {
            var method = node.Method;
            var type = method.DeclaringType;
            var pars = method.GetParameters();

            // for List/Dictionary [int/string]
            if (method.Name == "get_Item" && pars.Length == 1 &&
                (pars[0].ParameterType == typeof(int) || pars[0].ParameterType == typeof(string)))
            {
                obj = node.Object;
                idx = node.Arguments[0];
                return true;
            }

            //** // for Sql.Items(int)
            //** if (type == typeof(Sql) && method.Name == "Items" && 
            //**     pars.Length == 2 && pars[1].ParameterType == typeof(int))
            //** {
            //**     obj = node.Arguments[0];
            //**     idx = node.Arguments[1];
            //**     return true;
            //** }

            obj = null;
            idx = null;

            return false;
        }

        /// <summary>
        /// Visit expression but, if ensurePredicate = true, force expression be a predicate (appending ` = true`)
        /// </summary>
        private void VisitAsPredicate(Expression expr, bool ensurePredicate)
        {
            // apppend `= true` only if expression is path (MemberAccess), method call or constant
            ensurePredicate = ensurePredicate &&
                (expr.NodeType == ExpressionType.MemberAccess || expr.NodeType == ExpressionType.Call || expr.NodeType == ExpressionType.Invoke || expr.NodeType == ExpressionType.Constant);

            if (ensurePredicate)
            {
                _builder.Append("(");
                _builder.Append("(");
                base.Visit(expr);
                _builder.Append(")");
                _builder.Append(" = true)");
            }
            else
            {
                base.Visit(expr);
            }
        }

        /// <summary>
        /// Compile and execute expression (can be cached)
        /// </summary>
        private object Evaluate(Expression expr, params Type[] validTypes)
        {
            object value = null;

            if (expr.NodeType == ExpressionType.Constant)
            {
                var constant = (ConstantExpression)expr;

                value = constant.Value;
            }
            else
            {
                var func = Expression.Lambda(expr).Compile();

                value = func.DynamicInvoke();
            }

            // do some type validation to be ease to debug
            if (validTypes.Length > 0 && value == null)
            {
                throw new NotSupportedException($"Expression {expr} can't return null value");
            }

            if (validTypes.Length > 0 && validTypes.Any(x => x == value.GetType()) == false)
            {
                throw new NotSupportedException($"Expression {expr} must return on of this types: {string.Join(", ", validTypes.Select(x => $"`{x.Name}`"))}");
            }

            return value;
        }

        /// <summary>
        /// Try find a Type Resolver for declaring type
        /// </summary>
        private bool TryGetResolver(Type declaringType, out ITypeResolver typeResolver)
        {
            // get method declaring type - if is from any kind of list, read as Enumerable
            var isCollection = Reflection.IsCollection(declaringType);
            var isEnumerable = Reflection.IsEnumerable(declaringType);
            var isNullable = Reflection.IsNullable(declaringType);

            var type =
                isCollection ? typeof(ICollection) :
                isEnumerable ? typeof(Enumerable) :
                isNullable ? typeof(Nullable) :
                declaringType;

            return _resolver.TryGetValue(type, out typeResolver);
        }
    }
}