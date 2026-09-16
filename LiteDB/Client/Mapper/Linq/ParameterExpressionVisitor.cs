using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Class used to test in an Expression member expression is based on parameter `x => x.Name` or variable `x => externalVar`
    /// </summary>
    internal class ParameterExpressionVisitor : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _boundParameters = new HashSet<ParameterExpression>();

        public bool IsParameter { get; private set; } = false;

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_boundParameters.Contains(node)) this.IsParameter = true;

            return base.VisitParameter(node);
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            // Parameters owned by this subtree do not depend on an enclosing query row.
            var added = node.Parameters.Where(parameter => _boundParameters.Add(parameter)).ToArray();
            try
            {
                this.Visit(node.Body);
                return node;
            }
            finally
            {
                foreach (var parameter in added) _boundParameters.Remove(parameter);
            }
        }

        public static bool Test(Expression node)
        {
            var instance = new ParameterExpressionVisitor();

            instance.Visit(node);

            return instance.IsParameter;
        }
    }
}
