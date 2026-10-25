using System.Linq;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal sealed class SqlQueryTemplate
    {
        private readonly string _collection;
        private readonly Query _query;

        internal SqlQueryTemplate(string collection, Query query)
        {
            _collection = collection;
            // Capture before execution can write GROUP BY keys into parameters.
            // The template and all nested evaluators must retain no caller values.
            _query = Copy(query, null);
        }

        internal IBsonDataReader Execute(ILiteEngine engine, BsonDocument parameters)
        {
            LOG(_query.ExplainPlan ? "executing `EXPLAIN`" : "executing `SELECT`", "SQL");
            return Execute(engine, _collection, Copy(_query, parameters ?? new BsonDocument()));
        }

        internal static IBsonDataReader Execute(ILiteEngine engine, string collection, Query query)
        {
            if (collection != null) return engine.Query(collection, query);

            // SELECT without FROM still executes its expressions, including volatile
            // functions, with the current engine collation on every invocation.
            var collation = new Collation(engine.Pragma(Pragmas.COLLATION));
            var data = query.Select.Execute(collation)
                .Select(x => x.IsDocument ? x.AsDocument : new BsonDocument { ["expr"] = x }).FirstOrDefault();
            return new BsonDataReader(data, null);
        }

        private static Query Copy(Query source, BsonDocument parameters)
        {
            BsonExpression Bind(BsonExpression expression) => expression == null ? null :
                parameters == null ? expression.WithoutParameters() : expression.Bind(parameters);

            // This copies the logical clauses produced by the SQL SELECT grammar.
            // Engine plans, results, cursors, and publicly mutable Query instances
            // never enter the cache. Each execution gets its own clause lists.
            var rootGroup = source.GroupBy != null && source.Select.Type == BsonExpressionType.Path && source.Select.Source == "$";
            var query = new Query
            {
                // Root grouping previously wrote to the root singleton's document.
                // Keep that state separate from caller values used by residual filters.
                Select = rootGroup && parameters != null ? source.Select.Bind(new BsonDocument()) : Bind(source.Select),
                GroupBy = Bind(source.GroupBy), Having = Bind(source.Having),
                Offset = source.Offset, Limit = source.Limit, ForUpdate = source.ForUpdate,
                Into = source.Into, IntoAutoId = source.IntoAutoId, ExplainPlan = source.ExplainPlan
            };
            foreach (var expression in source.Includes) query.Includes.Add(Bind(expression));
            foreach (var expression in source.Where) query.Where.Add(Bind(expression));
            foreach (var order in source.OrderBy) query.OrderBy.Add(new QueryOrder(Bind(order.Expression), order.Order));
            return query;
        }
    }
}
