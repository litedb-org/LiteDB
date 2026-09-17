using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    public sealed partial class BsonExpression
    {
        private bool _selectAliasCompiled;

        internal void CompileSelectAlias(ExpressionContext context)
        {
            if (_selectAliasCompiled) return;
            var visitor = new ExactCompilationVisitor();
            visitor.Compile(this, context);
            _selectAliasCompiled = true;
        }

        private sealed class ExactCompilationVisitor : ExpressionVisitor
        {
            private readonly HashSet<BsonExpression> _visited = new HashSet<BsonExpression>();

            public void Compile(BsonExpression expression, ExpressionContext context)
            {
                if (expression._selectAliasCompiled || !_visited.Add(expression)) return;
                Visit(expression.Expression);
                if (expression.IsScalar)
                {
                    expression._funcScalar = System.Linq.Expressions.Expression.Lambda<BsonExpressionScalarDelegate>(
                        expression.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters).Compile();
                }
                else
                {
                    expression._funcEnumerable = System.Linq.Expressions.Expression.Lambda<BsonExpressionEnumerableDelegate>(
                        expression.Expression, context.Source, context.Root, context.Current, context.Collation, context.Parameters).Compile();
                }
                expression._selectAliasCompiled = true;
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                // Nested MAP/filter expressions own distinct parameter nodes and
                // may already hold a delegate from the display-Source cache.
                if (node.Value is BsonExpression nested && nested.SelectContext != null)
                    Compile(nested, nested.SelectContext);
                return node;
            }
        }
    }
}
