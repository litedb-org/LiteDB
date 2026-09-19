using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private bool TryVisitEnumerableQuantifier(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(Enumerable) || node.Arguments.Count != 2 ||
                (node.Method.Name != "Any" && node.Method.Name != "All") ||
                !(node.Arguments[1] is LambdaExpression lambda)) return false;

            var sourceType = node.Arguments[0].Type;
            if (sourceType.IsGenericType && sourceType.GetGenericTypeDefinition() == typeof(IGrouping<,>)) return false;

            // Keep the existing direct ANY/ALL translations and their index optimizations.
            if (CanUseDirectQuantifier(lambda)) return false;

            var scope = new QuantifierScopeVisitor(_rootParameter);
            scope.Visit(lambda);
            if (scope.HasOuterItemReference)
            {
                throw new NotSupportedException("Nested collection predicates cannot refer to an outer collection item.");
            }

            _builder.Append("MAP(");
            this.Visit(node.Arguments[0]);
            _builder.Append(" => ");
            _dbRefType = this.GetEnumerableReferenceType(node.Arguments[0]);
            this.Visit(lambda);
            _builder.Append(node.Method.Name == "Any" ? ") ANY = true" : ") ALL = true");
            return true;
        }

        private static bool CanUseDirectQuantifier(LambdaExpression lambda)
        {
            var parameter = lambda.Parameters[0];
            if (lambda.Body is BinaryExpression binary && binary.Left == parameter &&
                !ContainsParameter(binary.Right, parameter))
            {
                switch (binary.NodeType)
                {
                    case ExpressionType.Equal:
                    case ExpressionType.NotEqual:
                    case ExpressionType.GreaterThan:
                    case ExpressionType.GreaterThanOrEqual:
                    case ExpressionType.LessThan:
                    case ExpressionType.LessThanOrEqual: return true;
                }
            }
            if (lambda.Body is MethodCallExpression method && method.Object == parameter &&
                !method.Arguments.Any(argument => ContainsParameter(argument, parameter)) &&
                TryGetResolver(method.Method.DeclaringType, out var resolver))
            {
                var pattern = resolver.ResolveMethod(method.Method);
                return pattern != null && (pattern.StartsWith("# LIKE ", StringComparison.Ordinal) ||
                    pattern.StartsWith("# = ", StringComparison.Ordinal));
            }
            return false;
        }

        private static bool ContainsParameter(Expression expression, ParameterExpression parameter)
        {
            var visitor = new SpecificParameterVisitor(parameter);
            visitor.Visit(expression);
            return visitor.Found;
        }

        private sealed class SpecificParameterVisitor : ExpressionVisitor
        {
            private readonly ParameterExpression _parameter;
            public bool Found { get; private set; }
            public SpecificParameterVisitor(ParameterExpression parameter) => _parameter = parameter;
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _parameter) Found = true;
                return node;
            }
        }

        private sealed class QuantifierScopeVisitor : ExpressionVisitor
        {
            private readonly ParameterExpression _root;
            private IReadOnlyList<ParameterExpression> _parameters;
            public bool HasOuterItemReference { get; private set; }
            public QuantifierScopeVisitor(ParameterExpression root) => _root = root;
            protected override Expression VisitLambda<T>(Expression<T> node)
            {
                var previous = _parameters;
                _parameters = node.Parameters;
                this.Visit(node.Body);
                _parameters = previous;
                return node;
            }
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node != _root && (_parameters == null || !_parameters.Contains(node))) HasOuterItemReference = true;
                return node;
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
                if (!TryGetResolver(met.Method.DeclaringType, out var type))
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

    }
}
