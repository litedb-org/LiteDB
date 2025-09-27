using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent an GroupBy definition (is based on OrderByDefinition)
    /// </summary>
    internal class GroupBy
    {
        public BsonExpression Expression { get; }

        public BsonExpression Select { get; }

        public BsonExpression Having { get; }

        public OrderBy ProjectionOrderBy { get; }

        public GroupBy(BsonExpression expression, BsonExpression select, BsonExpression having, OrderBy projectionOrderBy)
        {
            this.Expression = expression;
            this.Select = select;
            this.Having = having;
            this.ProjectionOrderBy = projectionOrderBy;
        }
    }
}
