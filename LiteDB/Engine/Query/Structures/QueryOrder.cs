namespace LiteDB
{
    /// <summary>
    /// Represents a single ORDER BY segment containing the expression and direction.
    /// </summary>
    public class QueryOrder
    {
        public QueryOrder(BsonExpression expression, int order)
        {
            this.Expression = expression;
            this.Order = order;
        }

        public BsonExpression Expression { get; }

        public int Order { get; }
    }
}
