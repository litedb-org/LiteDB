using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private sealed class InvocationExpander : ExpressionVisitor
        {
            public override Expression Visit(Expression node)
            {
                // Closed captures are evaluated as one CLR expression by the existing
                // translator. Leave their invocation/evaluation order intact.
                if (node != null && node.NodeType != ExpressionType.Lambda &&
                    !ParameterExpressionVisitor.Test(node) && !ContainsServerRuntime(node)) return node;
                return base.Visit(node);
            }

            protected override Expression VisitInvocation(InvocationExpression node)
            {
                if (!(node.Expression is LambdaExpression lambda)) return base.VisitInvocation(node);

                var arguments = node.Arguments.Select(this.Visit).ToArray();
                if (arguments.Any(argument => !IsStableArgument(argument)))
                {
                    throw new NotSupportedException("Invoked query arguments must be parameters, constants, or document member paths.");
                }
                var substitutions = lambda.Parameters.Select((parameter, index) => new { parameter, argument = arguments[index] })
                    .ToDictionary(item => item.parameter, item => item.argument);
                // Normalize before any translation/scope analysis. Visiting an invoked
                // lambda directly would mistake its parameters for nested item scope.
                var body = new InvocationParameterSubstitution(substitutions).Visit(lambda.Body);
                return this.Visit(body);
            }
        }

        // Substitution may repeat, omit, or reorder arguments. Only accept values
        // whose evaluation is stable in a BSON query; calls and captured getters
        // need a let-binding that the query language does not currently provide.
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

            internal InvocationParameterSubstitution(Dictionary<ParameterExpression, Expression> substitutions)
            {
                _substitutions = substitutions;
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                return _substitutions.TryGetValue(node, out var argument) ? argument : node;
            }

            protected override Expression VisitLambda<T>(Expression<T> node)
            {
                // Rename nested binders before substituting. Otherwise an argument's
                // free parameter could be captured by an identical nested binder.
                var previous = node.Parameters.Select(parameter =>
                {
                    var present = _substitutions.TryGetValue(parameter, out var value);
                    return new { parameter, present, value };
                }).ToArray();
                var parameters = node.Parameters.Select(parameter => Expression.Parameter(
                    parameter.IsByRef ? parameter.Type.MakeByRefType() : parameter.Type, parameter.Name)).ToArray();
                for (var index = 0; index < parameters.Length; index++) _substitutions[node.Parameters[index]] = parameters[index];
                try
                {
                    return node.Update(this.Visit(node.Body), parameters);
                }
                finally
                {
                    foreach (var entry in previous)
                    {
                        if (entry.present) _substitutions[entry.parameter] = entry.value;
                        else _substitutions.Remove(entry.parameter);
                    }
                }
            }
        }
    }
}
