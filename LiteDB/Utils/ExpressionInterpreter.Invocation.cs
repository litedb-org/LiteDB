using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal static partial class ExpressionInterpreter
    {
        private static object EvaluateInvocation(InvocationExpression node, IList<ParameterExpression> parameters, object[] arguments)
        {
            var target = node.Expression is LambdaExpression inline ? inline : Evaluate(node.Expression, parameters, arguments);
            var values = node.Arguments.Select(argument => Evaluate(argument, parameters, arguments)).ToArray();
            if (target == null) throw new NullReferenceException();
            if (target is LambdaExpression lambda)
            {
                // Inner parameters shadow identical parameter objects in an outer
                // expression while retaining access to captured outer bindings.
                return Evaluate(lambda.Body, lambda.Parameters.Concat(parameters).ToArray(), values.Concat(arguments).ToArray());
            }
            if (target is Delegate function)
                return Invoke(function.GetType().GetMethod("Invoke"), function, values);
            throw Unsupported(node);
        }
    }
}
