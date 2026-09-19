using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class SqlParser
    {
        private static BsonExpression ResolveSelectAlias(BsonExpression select, BsonExpression order, string token)
        {
            // SQL aliases are standalone ORDER BY names. Other expressions keep
            // their ordinary source-document meaning; groups already sort outputs.
            string alias;
            if (order.Expression is MethodCallExpression member && member.Method.Name == "MEMBER_PATH" &&
                member.Arguments[0] is ParameterExpression root && root.Name == "root" &&
                member.Arguments[1] is ConstantExpression field && field.Value is string name)
            {
                alias = name;
            }
            else if (order.Expression is ConstantExpression &&
                string.Equals(order.Source, token, StringComparison.OrdinalIgnoreCase))
            {
                alias = token;
            }
            else return order;

            if (select.SelectAliases == null) return order;
            foreach (var item in select.SelectAliases)
            {
                if (string.Equals(item.Key, alias, StringComparison.OrdinalIgnoreCase) && !UsesVolatileMethod(item.Value))
                {
                    // Compile only a referenced alias. The projection must use the
                    // same exact constants even if its display Source collides in cache.
                    // Plain paths have a lossless index identity; computed aliases
                    // sort explicitly because display Source can round constants.
                    item.Value.RequiresExactSort = item.Value.Type != BsonExpressionType.Path;
                    if (item.Value.RequiresExactSort)
                    {
                        item.Value.CompileSelectAlias(select.SelectContext);
                        select.CompileSelectAlias(select.SelectContext);
                    }
                    else BsonExpression.Compile(item.Value, select.SelectContext);
                    return item.Value;
                }
            }
            return order;
        }
        private static bool UsesVolatileMethod(BsonExpression expression)
        {
            var visitor = new VolatileMethodVisitor();
            visitor.Visit(expression.Expression);
            return visitor.Found;
        }

        private sealed class VolatileMethodVisitor : ExpressionVisitor
        {
            private readonly HashSet<BsonExpression> _visited = new HashSet<BsonExpression>();
            public bool Found { get; private set; }
            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node.Value is BsonExpression nested && _visited.Add(nested)) Visit(nested.Expression);
                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.IsDefined(typeof(VolatileAttribute), false)) Found = true;
                return base.VisitMethodCall(node);
            }
        }
    }
}
