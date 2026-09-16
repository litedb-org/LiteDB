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
        private int _parameterIndex;

        internal LinqExpressionTranslator(BsonMapper mapper, Expression expression)
        {
            _mapper = mapper;
            _expression = expression as LambdaExpression ??
                throw new NotSupportedException($"Expression {expression} must be a lambda expression");
            _root = _expression.Parameters.First();
        }

        internal BsonExpression Resolve(bool predicate)
        {
            try
            {
                var result = Translate(_expression.Body);
                if (predicate && (result.Type == BsonExpressionType.Path || result.Type == BsonExpressionType.Call ||
                    result.Type == BsonExpressionType.Parameter))
                {
                    result = Group(Binary("=", result, Constant(true, _parameters), _context, _parameters));
                }
                BsonExpression.Compile(result, _context);
                return result;
            }
            catch (Exception exception)
            {
                throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {_expression} - {exception.Message}", exception);
            }
        }

        internal BsonExpression Translate(Expression expression)
        {
            switch (expression)
            {
                case LambdaExpression lambda: return Translate(lambda.Body);
                case ParameterExpression parameter: return Path("", parameter == _root, _scope, _context, _parameters);
                case ConstantExpression constant: return Bind(constant.Value);
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
            }
            throw new NotSupportedException($"Node {expression?.NodeType} is not supported in {_expression}: {expression}");
        }

        private BsonExpression TranslateMember(MemberExpression node)
        {
            if (TryGetResolver(node.Member.DeclaringType, out var resolver))
            {
                var binding = resolver.ResolveMember(node.Member);
                if (binding == null) throw Unsupported(node, node.Member.Name);
                return binding(new LinqBindingContext(this, node.Expression, new Expression[0]));
            }
            if (!ParameterExpressionVisitor.Test(node)) return Bind(Evaluate(node));
            if (node.Expression is ParameterExpression parameter)
            {
                return Path(ResolveMember(node.Member, out _), parameter == _root, _scope, _context, _parameters);
            }
            var parent = Translate(node.Expression);
            var name = ResolveMember(node.Member, out _);
            return Member(parent, name, _scope, _context);
        }

        private BsonExpression TranslateMethod(MethodCallExpression node)
        {
            if (node.Method.Name == "op_Implicit" && node.Arguments.Count == 1 && node.Type.IsGenericType &&
                (node.Type.GetGenericTypeDefinition().FullName == "System.Span`1" ||
                 node.Type.GetGenericTypeDefinition().FullName == "System.ReadOnlySpan`1"))
            {
                return Translate(node.Arguments[0]);
            }
            var arguments = node.Method.GetParameters();
            if (node.Method.Name == "get_Item" && arguments.Length == 1 &&
                (arguments[0].ParameterType == typeof(int) || arguments[0].ParameterType == typeof(string)))
            {
                var target = Translate(node.Object);
                var index = Evaluate(node.Arguments[0], typeof(string), typeof(int));
                return index is string field ? Member(target, field, _scope, _context) : Index(target, (int)index, null, _context);
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
                return Bind(Evaluate(node));
            }
            var binding = resolver.ResolveMethod(node.Method);
            if (binding == null) throw Unsupported(node, Reflection.MethodName(node.Method));
            return binding(new LinqBindingContext(this, node.Object, node.Arguments));
        }

        private BsonExpression TranslateBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Coalesce)
                return Call("COALESCE", new[] { Translate(node.Left), Translate(node.Right) }, _context, _parameters);
            if (node.NodeType == ExpressionType.ArrayIndex)
                return Index(Translate(node.Left), (int)Evaluate(node.Right, typeof(int)), null, _context);
            var logical = node.NodeType == ExpressionType.AndAlso || node.NodeType == ExpressionType.OrElse;
            var left = AsPredicate(node.Left, logical);
            var rightExpression = node.Right;
            if (!_mapper.EnumAsInteger && node.Left is UnaryExpression conversion && conversion.NodeType == ExpressionType.Convert &&
                conversion.Operand.Type.GetTypeInfo().IsEnum && conversion.Type == typeof(int))
            {
                rightExpression = Expression.Constant(Enum.GetName(conversion.Operand.Type, Evaluate(node.Right)));
            }
            return Group(Binary(Operator(node.NodeType), left, AsPredicate(rightExpression, logical), _context, _parameters));
        }

        private BsonExpression TranslateUnary(UnaryExpression node)
        {
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
            if (predicate && (node.NodeType == ExpressionType.MemberAccess || node.NodeType == ExpressionType.Call ||
                node.NodeType == ExpressionType.Invoke || node.NodeType == ExpressionType.Constant))
            {
                result = Group(Binary("=", Group(result), Constant(true, _parameters), _context, _parameters));
            }
            return result;
        }

        private BsonExpression Bind(object value)
        {
            var name = "p" + _parameterIndex++;
            _parameters[name] = value == null ? BsonValue.Null : value is string text ? new BsonValue(text) : _mapper.Serialize(value.GetType(), value);
            return Parameter(name, _context, _parameters);
        }

        private NotSupportedException Unsupported(Expression node, string member) =>
            new NotSupportedException($"Member/method {member} is not supported when converting {_expression}: {node}");

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
