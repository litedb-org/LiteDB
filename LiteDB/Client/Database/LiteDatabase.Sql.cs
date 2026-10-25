using System.Threading;

namespace LiteDB
{
    public partial class LiteDatabase
    {
        private SqlQueryCache _sqlQueryCache;

        private IBsonDataReader ExecuteSql(string command, BsonDocument parameters)
        {
            var eligible =
#if DEBUG || TESTING
                BsonExpression.CacheEnabled &&
#endif
                command.Length <= SqlQueryCache.MaximumCommandLength;
            var cache = Volatile.Read(ref _sqlQueryCache);
            var repeated = false;
            if (eligible && cache != null && cache.TryGet(command, out var template, out repeated))
                return template.Execute(_engine, parameters);

            var sql = new SqlParser(_engine, new Tokenizer(command), parameters, captureSelect: repeated);
            var reader = sql.Execute();
            if (eligible && sql.ParsedSelect)
            {
                if (cache == null)
                {
                    var created = new SqlQueryCache();
                    cache = Interlocked.CompareExchange(ref _sqlQueryCache, created, null) ?? created;
                }
                cache.Add(command, sql.SelectTemplate);
            }
            return reader;
        }
    }
}
