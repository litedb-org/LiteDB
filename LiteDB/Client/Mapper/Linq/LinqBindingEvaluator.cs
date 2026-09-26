using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    // Cached evaluators contain only CLR metadata and positions in the current
    // shape. Every constant, including closure instances, comes from this call.
    internal sealed class LinqBindingEvaluator : ExpressionVisitor
    {
        private readonly ParameterExpression _expressions = Expression.Parameter(typeof(List<Expression>), "expressions");
        private int _position;

        internal static Lazy<Func<List<Expression>, object>> Create(Expression binding, int position)
        {
            var target = binding;
            while (target is MemberExpression member) target = member.Expression;
            // Keep the existing reflection path for ordinary captured fields and
            // properties. Only bindings that would compile on every hit need this.
            if (target == null || target is ConstantExpression) return null;
            var compiler = new LinqBindingEvaluator();
            var body = compiler.Evaluate(binding, position);
            var lambda = Expression.Lambda<Func<List<Expression>, object>>(body, compiler._expressions);
            // Do not pay for a second compilation when a shape is used only once.
            // This tree has already replaced every caller-owned constant.
            return new Lazy<Func<List<Expression>, object>>(() => lambda.Compile(preferInterpretation: true));
        }

        private Expression Evaluate(Expression node, int position)
        {
            if (node == null) return Expression.Constant(null, typeof(object));
            if (node is ConstantExpression) return ReadConstant(position);
            if (node is MemberExpression member)
            {
                // Preserve reflection's null-target and property exception behavior
                // when the target itself contains a compiled method invocation.
                var target = Evaluate(member.Expression, position + 1);
                return member.Member is FieldInfo field ?
                    Expression.Call(Expression.Constant(field), typeof(FieldInfo).GetMethod("GetValue"), target) :
                    Expression.Call(Expression.Constant((PropertyInfo)member.Member),
                        typeof(PropertyInfo).GetMethod("GetValue", new[] { typeof(object), typeof(object[]) }),
                        target, Expression.Constant(null, typeof(object[])));
            }
            _position = position;
            var value = Expression.Convert(Visit(node), typeof(object));
            var error = Expression.Parameter(typeof(Exception), "error");
            // The uncached evaluator uses DynamicInvoke, which wraps exceptions.
            return Expression.TryCatch(value, Expression.Catch(error,
                Expression.Throw(Expression.New(typeof(TargetInvocationException).GetConstructor(new[] { typeof(Exception) }), error), typeof(object))));
        }

        private Expression ReadConstant(int position)
        {
            var current = Expression.Property(_expressions, typeof(List<Expression>).GetProperty("Item"), Expression.Constant(position));
            return Expression.Property(Expression.Convert(current, typeof(ConstantExpression)), typeof(ConstantExpression).GetProperty("Value"));
        }

        public override Expression Visit(Expression node)
        {
            // Match LinqQueryShape's traversal, including repeated references and
            // null targets. IndexOf would alias distinct occurrences on later calls.
            var position = _position++;
            return node is ConstantExpression ? Expression.Convert(ReadConstant(position), node.Type) : base.Visit(node);
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            _position++; // LinqQueryShape records assignment metadata separately.
            return base.VisitMemberAssignment(node);
        }
    }
}
