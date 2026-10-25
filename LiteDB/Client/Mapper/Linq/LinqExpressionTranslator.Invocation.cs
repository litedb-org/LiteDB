using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal sealed partial class LinqExpressionTranslator
    {
        private sealed class InvocationExpander : ExpressionVisitor
        {
            public override Expression Visit(Expression node)
            {
                if (node != null && node.NodeType != ExpressionType.Lambda &&
                    !ParameterExpressionVisitor.Test(node) && !ContainsServerRuntime(node)) return node;
                return base.Visit(node);
            }

            protected override Expression VisitInvocation(InvocationExpression node)
            {
                if (!(node.Expression is LambdaExpression lambda)) return base.VisitInvocation(node);
                var arguments = node.Arguments.Select(Visit).ToArray();
                if (arguments.Any(argument => !IsStableArgument(argument)))
                    throw new NotSupportedException("Invoked query arguments must be parameters, constants, or document member paths.");
                var substitutions = lambda.Parameters.Select((parameter, index) =>
                    new { Parameter = parameter, Argument = arguments[index] })
                    .ToDictionary(item => item.Parameter, item => item.Argument);
                var body = new InvocationParameterSubstitution(substitutions).Visit(lambda.Body);
                return Visit(body);
            }
        }

        private static bool IsStableArgument(Expression argument)
        {
            if (argument is ParameterExpression || argument is ConstantExpression) return true;
            if (!(argument is MemberExpression member)) return false;
            var source = member.Expression;
            while (source is MemberExpression parent) source = parent.Expression;
            return source is ParameterExpression;
        }

        private sealed class InvocationParameterSubstitution : ExpressionVisitor
        {
            private readonly Dictionary<ParameterExpression, Expression> _substitutions;

            internal InvocationParameterSubstitution(Dictionary<ParameterExpression, Expression> substitutions) =>
                _substitutions = substitutions;

            protected override Expression VisitParameter(ParameterExpression node) =>
                _substitutions.TryGetValue(node, out var argument) ? argument : node;

            protected override Expression VisitLambda<T>(Expression<T> node)
            {
                var previous = node.Parameters.Select(parameter =>
                {
                    var present = _substitutions.TryGetValue(parameter, out var value);
                    return new { Parameter = parameter, Present = present, Value = value };
                }).ToArray();
                var parameters = node.Parameters.Select(parameter =>
                    Expression.Parameter(parameter.IsByRef ? parameter.Type.MakeByRefType() : parameter.Type, parameter.Name)).ToArray();
                for (var index = 0; index < parameters.Length; index++)
                    _substitutions[node.Parameters[index]] = parameters[index];
                try
                {
                    return node.Update(Visit(node.Body), parameters);
                }
                finally
                {
                    foreach (var entry in previous)
                    {
                        if (entry.Present) _substitutions[entry.Parameter] = entry.Value;
                        else _substitutions.Remove(entry.Parameter);
                    }
                }
            }
        }
    }
}
