using System.Collections.Generic;

namespace LiteDB
{
    // These immutable, parameter-free logical templates are shared across mappers
    // and query types. Each call binds its own parameter document because GROUP BY
    // writes its key there. Index selection and aggregate input remain per query.
    internal static class QueryAggregateExpressions
    {
        internal static readonly BsonExpression Count = Create("count", "COUNT");
        internal static readonly BsonExpression Exists = Create("exists", "ANY");

        private static BsonExpression Create(string field, string method)
        {
            var parameters = new BsonDocument();
            var context = new ExpressionContext();
            var itemContext = new ExpressionContext();
            var id = BsonExpressionFactory.Path("_id", false, DocumentScope.Source, itemContext, parameters);
            BsonExpression.Compile(id, itemContext);
            var source = BsonExpressionFactory.Source(context, parameters);
            var values = BsonExpressionFactory.MapPath(source, id, context);
            var aggregate = BsonExpressionFactory.Call(method, new[] { values }, context, parameters);
            var select = BsonExpressionFactory.Document(new[] { new KeyValuePair<string, BsonExpression>(field, aggregate) }, parameters);
            BsonExpression.Compile(select, context);
            return select;
        }
    }
}
