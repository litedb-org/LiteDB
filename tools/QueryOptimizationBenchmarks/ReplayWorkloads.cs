using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class ReplayWorkloads
{
    internal static void Run(Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        TryMeasure("replay-aggregates-sql", db =>
        {
            using var reader = db.Execute("SELECT SUM(*._id) AS total, MAX(*._id) AS largest FROM rows");
            var result = reader.ToEnumerable().Single();
            return result["total"].AsInt64 + result["largest"].AsInt32;
        });
        TryMeasure("replay-group-constant-linq", db => db.GetCollection<Program.Row>("rows").Query().GroupBy(x => 0).Count());
        TryMeasure("replay-computed-sort", db => db.GetCollection("rows").Query().OrderBy("_id % 3").ThenByDescending("_id")
            .Select("{ id: _id }").Limit(10).ToDocuments().Sum(x => x["id"].AsInt64));

        void TryMeasure(string name, Func<LiteDatabase, long> operation)
        {
            if (filter != null && !name.StartsWith(filter, StringComparison.Ordinal)) return;
            // A broken baseline lookup faults its engine. Isolate each reproducer;
            // database construction and inserts remain outside query timing.
            using var isolated = new LiteDatabase(":memory:");
            isolated.GetCollection("rows").InsertBulk(Enumerable.Range(1, 20000)
                .Select(i => new BsonDocument { ["_id"] = i }));
            try { measure(name, 3, i => operation(isolated)); }
            catch (LiteException exception)
            {
                // Record failures explicitly; failed queries are never timing results.
                plans[name + "-error"] = exception.Message;
            }
        }
    }
}
