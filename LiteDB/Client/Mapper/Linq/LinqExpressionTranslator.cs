using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal sealed partial class LinqExpressionTranslator
    {
        private readonly BsonMapper _mapper;
        private readonly LambdaExpression _expression;
        private readonly ParameterExpression _root;
        private readonly BsonDocument _parameters = new BsonDocument();
        private ExpressionContext _context = new ExpressionContext();
        private DocumentScope _scope = DocumentScope.Root;
        private Type _dbRefType;
        private readonly Dictionary<ParameterExpression, Type> _lambdaReferences = new Dictionary<ParameterExpression, Type>();
        private int _parameterIndex;

        internal List<Expression> Bindings { get; }
        internal List<LinqMemberGuard> MemberGuards { get; }

        internal LinqExpressionTranslator(BsonMapper mapper, Expression expression, bool recordBindings = false)
        {
            _mapper = mapper;
            var lambda = expression as LambdaExpression ??
                throw new NotSupportedException($"Expression {expression} must be a lambda expression");
            _expression = (LambdaExpression)new InvocationExpander().Visit(lambda);
            _root = _expression.Parameters.First();
            if (recordBindings)
            {
                Bindings = new List<Expression>();
                MemberGuards = new List<LinqMemberGuard>();
            }
        }

        internal BsonExpression Resolve(bool predicate)
        {
            try
            {
                ValidateCapturedRuntimeBranches(_expression);
                var result = Translate(_expression.Body);
                if (predicate && (result.Type == BsonExpressionType.Path || result.Type == BsonExpressionType.Call ||
                    result.Type == BsonExpressionType.Parameter))
                {
                    result = Group(Binary("=", result, Constant(true, _parameters), _context, _parameters));
                }
                BsonExpression.Compile(result, _context);
                return result;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (NotSupportedException exception) when (exception.Message.StartsWith("Empty row dictionary keys", StringComparison.Ordinal))
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {_expression} - {exception.Message}", exception);
            }
        }

        internal BsonExpression Translate(Expression expression)
        {
            if (!(expression is LambdaExpression) && !IsSpanImplicitConversion(expression) &&
                !ParameterExpressionVisitor.Test(expression) && !ContainsServerRuntime(expression) &&
                (expression is InvocationExpression || ContainsClosedElement(expression)))
                return Bind(Evaluate(expression), expression);
            switch (expression)
            {
                case LambdaExpression lambda: return Translate(lambda.Body);
                case ParameterExpression parameter: return Path("", parameter == _root, _scope, _context, _parameters);
                case ConstantExpression constant: return Bind(constant.Value, constant);
                case MemberExpression member: return TranslateMember(member);
                case MethodCallExpression method: return TranslateMethod(method);
                case BinaryExpression binary: return TranslateBinary(binary);
                case UnaryExpression unary: return TranslateUnary(unary);
                case ConditionalExpression conditional:
                    return Call("IIF", new[] { Translate(conditional.Test), Translate(conditional.IfTrue), Translate(conditional.IfFalse) }, _context, _parameters);
                case NewExpression creation: return TranslateNew(creation);
                case MemberInitExpression initializer: return TranslateInitializer(initializer);
                case NewArrayExpression array: return Array(array.Expressions.Select(Translate), _parameters);
                case InvocationExpression invocation:
                    if (invocation.Expression is LambdaExpression target)
                    {
                        var body = new InvocationArguments(target.Parameters, invocation.Arguments).Visit(target.Body);
                        return Translate(body);
                    }
                    break;
                case IndexExpression index: return TranslateIndex(index);
            }
            throw new NotSupportedException($"Node {expression?.NodeType} is not supported in {_expression}: {expression}");
        }

        private BsonExpression TranslateMember(MemberExpression node)
        {
            if (!ParameterExpressionVisitor.Test(node) && ContainsServerRuntime(node) &&
                !TryGetResolver(node.Member.DeclaringType, out _) && ContainsClosedElement(node))
                throw new NotSupportedException("Captured element members containing server runtime expressions are not supported.");
            if (TryGetResolver(node.Member.DeclaringType, out var resolver))
            {
                if (resolver is GroupingResolver && !ParameterExpressionVisitor.Test(node))
                    return Bind(Evaluate(node), node);
                var binding = resolver.ResolveMember(node.Member);
                if (binding == null) throw Unsupported(node, node.Member.Name);
                return binding(new LinqBindingContext(this, node.Expression, new Expression[0]));
            }
            if (!ParameterExpressionVisitor.Test(node)) return Bind(Evaluate(node), node);
            if (node.Expression is ParameterExpression parameter)
            {
                if (_lambdaReferences.TryGetValue(parameter, out var referenceType)) _dbRefType = referenceType;
                return Path(ResolveMember(node.Member, parameter.Type, out _), parameter == _root, _scope, _context, _parameters);
            }
            var parent = Translate(node.Expression);
            var owner = node.Expression;
            while (owner is UnaryExpression conversion && conversion.Method == null &&
                (conversion.NodeType == ExpressionType.Convert || conversion.NodeType == ExpressionType.TypeAs) &&
                conversion.Type.IsAssignableFrom(conversion.Operand.Type))
            {
                owner = conversion.Operand;
            }
            var name = ResolveMember(node.Member, owner.Type, out _);
            return Member(parent, name, _scope, _context);
        }

        private BsonExpression TranslateMethod(MethodCallExpression node)
        {
            if (TryTranslateEnumEquals(node, out var enumEquals)) return enumEquals;
            if (TryTranslateOrdinalStringEquals(node, out var ordinalEquals)) return ordinalEquals;
            if (node.Method.DeclaringType == typeof(string) && node.Method.Name == nameof(string.Equals) &&
                node.Arguments.Count > 0 && node.Arguments[node.Arguments.Count - 1].Type == typeof(StringComparison) &&
                !ParameterExpressionVisitor.Test(node.Arguments[node.Arguments.Count - 1]))
            {
                // Ordinal equality adds a plain equality term so the query can seek an
                // index. A template produced for any other captured mode therefore
                // cannot be reused when that captured value later becomes Ordinal.
                Bindings?.Add(null);
            }
            if (node.Method.Name == "op_Implicit" && node.Arguments.Count == 1 && node.Type.IsGenericType &&
                (node.Type.GetGenericTypeDefinition().FullName == "System.Span`1" ||
                 node.Type.GetGenericTypeDefinition().FullName == "System.ReadOnlySpan`1"))
            {
                return Translate(node.Arguments[0]);
            }
            var arguments = node.Method.GetParameters();
            if ((node.Method.Name == "ElementAt" || node.Method.Name == "ElementAtOrDefault") && node.Arguments.Count == 2)
                ValidateIndexAccess(node.Arguments[0], new[] { node.Arguments[1] });
            if (node.Method.Name == "get_Item" && arguments.Length == 1 &&
                (arguments[0].ParameterType == typeof(int) || arguments[0].ParameterType == typeof(string)))
            {
                ValidateIndexAccess(node.Object, node.Arguments);
                var target = Translate(node.Object);
                var index = Evaluate(node.Arguments[0], typeof(string), typeof(int));
                if (index is string field)
                {
                    if (field.Length == 0) throw new NotSupportedException("Empty row dictionary keys are not supported by BSON paths.");
                    return Member(target, field, _scope, _context);
                }
                return Index(target, (int)index, null, _context);
            }
            var hasResolver = TryGetResolver(node.Method.DeclaringType, out var resolver);
            if (node.Method.DeclaringType == typeof(Enumerable) && node.Arguments.Count > 0 &&
                node.Arguments[0].Type.IsGenericType && node.Arguments[0].Type.GetGenericTypeDefinition() == typeof(IGrouping<,>))
            {
                resolver = _resolvers[typeof(IGrouping<,>)];
                hasResolver = true;
            }
            if (!hasResolver)
            {
                if (ParameterExpressionVisitor.Test(node)) throw Unsupported(node, node.Method.Name);
                return Bind(Evaluate(node), node);
            }
            var binding = resolver.ResolveMethod(node.Method);
            if (binding == null)
            {
                if (!ParameterExpressionVisitor.Test(node) && !ContainsClosedElement(node)) return Bind(Evaluate(node), node);
                throw Unsupported(node, Reflection.MethodName(node.Method));
            }
            var result = binding(new LinqBindingContext(this, node.Object, node.Arguments));
            if (node.Method.DeclaringType == typeof(string) && !node.Method.IsStatic && node.Arguments.Count > 0 &&
                ParameterExpressionVisitor.Test(node.Arguments[0]) &&
                (node.Method.Name == nameof(string.StartsWith) || node.Method.Name == nameof(string.EndsWith) ||
                 node.Method.Name == nameof(string.Contains) || node.Method.Name == nameof(string.IndexOf)))
            {
                var text = Translate(node.Arguments[0]);
                var isString = Call("IS_STRING", new[] { text }, _context, _parameters);
                if (node.Method.Name == nameof(string.IndexOf))
                    return Call("IIF", new[] { isString, result, Constant(BsonValue.Null, _parameters) }, _context, _parameters);
                return Group(Binary("AND", Group(Binary("=", isString, Constant(true, _parameters), _context, _parameters)),
                    Group(Binary("=", result, Constant(true, _parameters), _context, _parameters)), _context, _parameters));
            }
            return result;
        }

        private bool TryTranslateOrdinalStringEquals(MethodCallExpression node, out BsonExpression result)
        {
            result = null;
            if (node.Method.DeclaringType != typeof(string) || node.Method.Name != nameof(string.Equals) ||
                node.Arguments.Count == 0) return false;
            var modeExpression = node.Arguments[node.Arguments.Count - 1];
            if (modeExpression.Type != typeof(StringComparison) || ParameterExpressionVisitor.Test(modeExpression) ||
                !StringComparison.Ordinal.Equals(Evaluate(modeExpression))) return false;

            BsonExpression left;
            BsonExpression right;
            BsonExpression exact;
            if (node.Method.IsStatic)
            {
                left = Translate(node.Arguments[0]);
                right = Translate(node.Arguments[1]);
                exact = Call("STRING_EQUALS", new[] { left, right, Translate(modeExpression) }, _context, _parameters);
            }
            else
            {
                left = Translate(node.Object);
                right = Translate(node.Arguments[0]);
                exact = Call("STRING_EQUALS_INSTANCE", new[] { left, right, Translate(modeExpression) }, _context, _parameters);
            }
            var plain = Group(Binary("=", left, right, _context, _parameters));
            var exactPredicate = Group(Binary("=", exact, Constant(true, _parameters), _context, _parameters));
            result = Group(Binary("AND", plain, exactPredicate, _context, _parameters));
            Bindings?.Add(null); // The selected comparison mode changes the IR shape.
            return true;
        }

        private BsonExpression TranslateBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Coalesce)
                return Call("COALESCE", new[] { Translate(node.Left), Translate(node.Right) }, _context, _parameters);
            if (node.NodeType == ExpressionType.ArrayIndex)
            {
                ValidateIndexAccess(node.Left, new[] { node.Right });
                return Index(Translate(node.Left), (int)Evaluate(node.Right, typeof(int)), null, _context);
            }
            var logical = node.NodeType == ExpressionType.AndAlso || node.NodeType == ExpressionType.OrElse;
            var leftExpression = node.Left;
            var rightExpression = node.Right;
            if (!_mapper.EnumAsInteger && !logical)
            {
                var leftEnum = GetConvertedEnum(leftExpression);
                var rightEnum = GetConvertedEnum(rightExpression);
                var leftDependsOnRow = ParameterExpressionVisitor.Test(leftExpression);
                var rightDependsOnRow = ParameterExpressionVisitor.Test(rightExpression);
                if (leftDependsOnRow && rightDependsOnRow && leftEnum != rightEnum &&
                    (leftEnum != null || rightEnum != null))
                    throw new NotSupportedException($"Enums are stored by name, so `{node}` cannot compare an enum with a numeric member.");
                if (node.NodeType != ExpressionType.Equal && node.NodeType != ExpressionType.NotEqual &&
                    (leftEnum != null || rightEnum != null))
                    throw new InvalidOperationException("Numeric operations are not supported for enums stored by name.");
                if (leftDependsOnRow && leftEnum != null && !rightDependsOnRow)
                    rightExpression = Expression.Constant(Enum.ToObject(leftEnum, Evaluate(rightExpression)), leftEnum);
                else if (!leftDependsOnRow && rightDependsOnRow && rightEnum != null)
                    leftExpression = Expression.Constant(Enum.ToObject(rightEnum, Evaluate(leftExpression)), rightEnum);
                else
                {
                    if (!leftDependsOnRow && rightEnum == null && leftEnum != null)
                        leftExpression = Expression.Constant(Evaluate(leftExpression));
                    if (!rightDependsOnRow && leftEnum == null && rightEnum != null)
                        rightExpression = Expression.Constant(Evaluate(rightExpression));
                }
            }
            return Group(Binary(Operator(node.NodeType), AsPredicate(leftExpression, logical),
                AsPredicate(rightExpression, logical), _context, _parameters));
        }

        private bool TryTranslateEnumEquals(MethodCallExpression node, out BsonExpression result)
        {
            result = null;
            var declaringType = node.Method.DeclaringType;
            if ((declaringType != typeof(Enum) && declaringType != typeof(object) && declaringType != typeof(ValueType)) ||
                node.Method.Name != nameof(Enum.Equals) || node.Object == null || node.Arguments.Count != 1 ||
                !ParameterExpressionVisitor.Test(node)) return false;

            var left = UnwrapEnumBoxing(node.Object);
            var enumType = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
            if (!enumType.GetTypeInfo().IsEnum) return false;
            var right = UnwrapEnumBoxing(node.Arguments[0]);
            if (!ParameterExpressionVisitor.Test(right))
            {
                var value = Evaluate(right);
                if (value == null || value.GetType() != enumType)
                {
                    result = Constant(false, _parameters);
                    return true;
                }
                right = Expression.Constant(value, enumType);
            }
            else
            {
                var rightType = Nullable.GetUnderlyingType(right.Type) ?? right.Type;
                if (rightType != enumType)
                {
                    if (!rightType.GetTypeInfo().IsEnum) return false;
                    result = Constant(false, _parameters);
                    return true;
                }
            }
            result = Group(Binary("=", Translate(left), Translate(right), _context, _parameters));
            return true;
        }

        private static Expression UnwrapEnumBoxing(Expression expression)
        {
            while (expression is UnaryExpression conversion && conversion.NodeType == ExpressionType.Convert &&
                conversion.Method == null && (conversion.Type == typeof(object) || conversion.Type == typeof(Enum) ||
                    conversion.Type == typeof(ValueType))) expression = conversion.Operand;
            return expression;
        }

        private static Type GetConvertedEnum(Expression expression)
        {
            if (expression.NodeType != ExpressionType.Convert && expression.NodeType != ExpressionType.ConvertChecked) return null;
            while (expression.NodeType == ExpressionType.Convert || expression.NodeType == ExpressionType.ConvertChecked)
                expression = ((UnaryExpression)expression).Operand;
            var type = Nullable.GetUnderlyingType(expression.Type) ?? expression.Type;
            return type.GetTypeInfo().IsEnum ? type : null;
        }

        private BsonExpression TranslateUnary(UnaryExpression node)
        {
            if (ContainsServerRuntime(node) && !IsSafeRuntimeUnary(node))
                throw new NotSupportedException("Captured unary expressions containing unsupported server runtime combinations are not supported.");
            if (node.NodeType == ExpressionType.Not)
            {
                var operand = Translate(node.Operand);
                return node.Operand.NodeType == ExpressionType.MemberAccess ?
                    Group(Binary("=", operand, Constant(false, _parameters), _context, _parameters)) :
                    Binary("=", Group(operand), Constant(false, _parameters), _context, _parameters);
            }
            if (node.NodeType == ExpressionType.ArrayLength)
                return Call("LENGTH", new[] { Translate(node.Operand) }, _context, _parameters);
            if (node.NodeType == ExpressionType.Convert &&
                (node.Operand.Type == typeof(double) || node.Operand.Type == typeof(decimal)) &&
                (node.Type == typeof(int) || node.Type == typeof(long)))
            {
                return Call(node.Type == typeof(int) ? "INT32" : "INT64", new[] { Translate(node.Operand) }, _context, _parameters);
            }
            return Translate(node.Operand);
        }

        private BsonExpression AsPredicate(Expression node, bool predicate)
        {
            var result = Translate(node);
            if (predicate && result.Type != BsonExpressionType.And && result.Type != BsonExpressionType.Or &&
                (node.NodeType == ExpressionType.MemberAccess || node.NodeType == ExpressionType.Call ||
                 node.NodeType == ExpressionType.Invoke || node.NodeType == ExpressionType.Constant))
            {
                result = Group(Binary("=", Group(result), Constant(true, _parameters), _context, _parameters));
            }
            return result;
        }

        private BsonExpression Bind(object value, Expression origin = null)
        {
            Bindings?.Add(origin);
            var name = "p" + _parameterIndex++;
            _parameters[name] = value == null ? BsonValue.Null : value is BsonValue bson ? bson :
                value is string text ? new BsonValue(text) : _mapper.Serialize(value.GetType(), value);
            return Parameter(name, _context, _parameters);
        }

        private NotSupportedException Unsupported(Expression node, string member) =>
            new NotSupportedException($"Member/method {member} is not supported when converting {_expression}: {node}");

        private static bool IsSpanImplicitConversion(Expression expression) =>
            expression is MethodCallExpression method && method.Method.Name == "op_Implicit" &&
            method.Arguments.Count == 1 && method.Type.IsGenericType &&
            (method.Type.GetGenericTypeDefinition().FullName == "System.Span`1" ||
             method.Type.GetGenericTypeDefinition().FullName == "System.ReadOnlySpan`1");

        private sealed class InvocationArguments : ExpressionVisitor
        {
            private readonly IList<ParameterExpression> _parameters;
            private readonly IList<Expression> _arguments;
            internal InvocationArguments(IList<ParameterExpression> parameters, IList<Expression> arguments)
            {
                _parameters = parameters;
                _arguments = arguments;
            }
            protected override Expression VisitParameter(ParameterExpression node)
            {
                var index = _parameters.IndexOf(node);
                return index < 0 ? node : _arguments[index];
            }
        }
    }
}
