using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    internal delegate BsonExpression LinqExpressionBinding(LinqExpressionTranslator.LinqBindingContext context);

    internal sealed partial class LinqExpressionTranslator
    {
        // Resolvers construct semantic nodes. Arguments are visited in binding order,
        // preserving the parameter slots and evaluation behavior of the public API.
        internal sealed class LinqBindingContext
        {
            private readonly LinqExpressionTranslator _translator;
            private readonly Expression _object;
            private readonly IList<Expression> _arguments;

            internal LinqBindingContext(LinqExpressionTranslator translator, Expression obj, IList<Expression> arguments)
            {
                _translator = translator;
                _object = obj;
                _arguments = arguments;
            }

            internal BsonExpression Object() => _translator.Translate(_object);
            internal BsonExpression Argument(int index) => _translator.Translate(_arguments[index]);
            internal BsonExpression Constant(BsonValue value) => BsonExpressionFactory.Constant(value, _translator._parameters);
            internal BsonExpression Parameter(string name) => BsonExpressionFactory.Parameter(name, _translator._context, _translator._parameters);
            internal BsonExpression Source() => BsonExpressionFactory.Source(_translator._context, _translator._parameters);
            internal BsonExpression Group(BsonExpression expression) => BsonExpressionFactory.Group(expression);
            internal BsonExpression Items(BsonExpression expression) => BsonExpressionFactory.ConvertToEnumerable(expression);
            internal BsonExpression Call(string name, params BsonExpression[] arguments) =>
                BsonExpressionFactory.Call(name, arguments, _translator._context, _translator._parameters);
            internal BsonExpression Binary(string operation, BsonExpression left, BsonExpression right) =>
                BsonExpressionFactory.Binary(operation, left, right, _translator._context, _translator._parameters);
            internal BsonExpression Index(BsonExpression expression, int index) =>
                BsonExpressionFactory.Index(expression, index, null, _translator._context);
            internal BsonExpression Index(BsonExpression expression, BsonExpression parameter)
            {
                // Enumerable.ElementAt uses a parameter slot; array/list indexers use
                // evaluated constant indices, matching the existing LINQ contract.
                BsonExpression.Compile(parameter, _translator._context);
                return parameter.Type == BsonExpressionType.Parameter ?
                    BsonExpressionFactory.Index(expression, 0, parameter, _translator._context) :
                    BsonExpressionFactory.FilterPath(expression, parameter, _translator._context);
            }

            internal BsonExpression Function(string name, BsonExpression left, int lambdaIndex)
            {
                var context = _translator._context;
                var scope = _translator._scope;
                BsonExpression right;
                try
                {
                    _translator._context = new ExpressionContext();
                    _translator._scope = left.Type == BsonExpressionType.Source ? DocumentScope.Source : DocumentScope.Current;
                    right = _translator.Translate(_arguments[lambdaIndex]);
                    BsonExpression.Compile(right, _translator._context);
                }
                finally
                {
                    _translator._context = context;
                    _translator._scope = scope;
                }
                return BsonExpressionFactory.Function(name, name == "MAP" ? BsonExpressionType.Map : BsonExpressionType.Filter,
                    left, right, new BsonExpression[0], context, _translator._parameters);
            }

            internal BsonExpression Quantifier(string quantifier, BsonExpression left, int lambdaIndex)
            {
                if (!(_arguments[lambdaIndex] is LambdaExpression lambda)) throw _translator.Unsupported(_arguments[lambdaIndex], quantifier);
                if (lambda.Body is BinaryExpression binary)
                {
                    if (binary.Left.NodeType != ExpressionType.Parameter)
                        throw new LiteException(0, "Any/All requires simple parameter on left side. Eg: `x => x.Phones.Select(p => p.Number).Any(n => n > 5)`");
                    return Binary(quantifier + " " + Operator(binary.NodeType), left, _translator.Translate(binary.Right));
                }
                if (lambda.Body is MethodCallExpression method && method.Object is ParameterExpression &&
                    TryGetResolver(method.Method.DeclaringType, out var resolver))
                {
                    var binding = resolver.ResolveMethod(method.Method);
                    if (binding != null)
                    {
                        var predicate = binding(new LinqBindingContext(_translator, method.Object, method.Arguments));
                        if (predicate.IsPredicate && predicate.Left?.Source == "@")
                        {
                            var operation = predicate.Type == BsonExpressionType.Like ? "LIKE" :
                                predicate.Type == BsonExpressionType.Equal ? "=" : null;
                            if (operation != null) return Binary(quantifier + " " + operation, left, predicate.Right);
                        }
                    }
                }
                throw _translator.Unsupported(lambda, "Any/All requires a simple predicate parameter");
            }
        }
    }
}
