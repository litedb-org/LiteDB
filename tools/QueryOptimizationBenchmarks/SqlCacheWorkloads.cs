using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;

internal static class SqlCacheWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter != null && !filter.StartsWith("sqlcache", StringComparison.Ordinal) &&
            !"sqlcache".StartsWith(filter, StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        const string point = "SELECT $ FROM rows WHERE _id = @id";
        measure("sqlcache-point", 4000, i => Read(point, new BsonDocument { ["id"] = i % 20000 + 1 }));
        measure("sqlcache-combined", 1000, i => Read(
            "SELECT $ FROM rows WHERE City = @city AND Score >= @minimum LIMIT 1",
            new BsonDocument { ["city"] = "City" + i % 1000, ["minimum"] = i % 500 }));
        measure("sqlcache-projection", 1000, i => Read(
            "SELECT { _id: _id, Name: Name } FROM rows WHERE City = @city ORDER BY _id DESC LIMIT 5",
            new BsonDocument { ["city"] = "City" + i % 1000 }));
        measure("sqlcache-bounded-range", 1000, i => Read(
            "SELECT $ FROM rows WHERE Score >= @low AND Score < @high",
            new BsonDocument { ["low"] = i + 10000, ["high"] = i + 10010 }));
        measure("sqlcache-nested-projection", 1000, i => Read(
            "SELECT { _id: _id, values: ARRAY(MAP([1,2,3] => @ + @delta)) } FROM rows WHERE _id = @id",
            new BsonDocument { ["id"] = i + 1, ["delta"] = i }));
        measure("sqlcache-grouped-page", 1000, i => Read(
            "SELECT { _id: @key, n: COUNT(*), total: SUM(*.Score) + @delta } FROM rows WHERE Score >= @low AND Score < @high GROUP BY City",
            new BsonDocument { ["low"] = 10000, ["high"] = 10020, ["delta"] = i }, "n"));
        measure("sqlcache-count", 1000, i => Read(
            "SELECT { n: COUNT(*) } FROM rows WHERE Score >= @low AND Score < @high",
            new BsonDocument { ["low"] = 10000, ["high"] = 10100 }, "n"));
        measure("sqlcache-scalar-no-from", 4000, i => Read(
            "SELECT { n: @value + 1 }", new BsonDocument { ["value"] = i }, "n"));
        var commands = Enumerable.Range(0, 256).Select(i =>
            "SELECT { _id: _id, marker: " + i + " } FROM rows WHERE _id = @id").ToArray();
        measure("sqlcache-64-statements", 1024, i => Read(commands[i % 64], new BsonDocument { ["id"] = i % 1024 + 1 }));
        measure("sqlcache-256-churn-control", 1024, i => Read(commands[i % 256], new BsonDocument { ["id"] = i % 1024 + 1 }));
        var tagged = Enumerable.Range(0, 256).Select(i => point + " -- query " + i).ToArray();
        measure("sqlcache-tagged-churn-control", 1024, i => Read(tagged[i % 256], new BsonDocument { ["id"] = i % 1024 + 1 }));
        measure("sqlcache-textreader-control", 4000, i => Read(point,
            new BsonDocument { ["id"] = i % 20000 + 1 }, fresh: true));
        measure("sqlcache-linq-control", 4000, i =>
        {
            var id = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
        });
        measure("sqlcache-scan-control", 3, i => Read("SELECT $ FROM rows WHERE Name LIKE 'Person1%'"));

        long Read(string sql, BsonDocument parameters = null, string field = "_id", bool fresh = false)
        {
            using var reader = fresh ? db.Execute(new StringReader(sql), parameters) : db.Execute(sql, parameters);
            long sum = 0;
            while (reader.Read())
            {
                var current = reader.Current;
                sum += current[field].AsInt32;
                if (current.AsDocument.ContainsKey("values"))
                    foreach (var value in current["values"].AsArray) sum += value.AsInt32;
                if (current.AsDocument.ContainsKey("total")) sum += current["total"].AsInt32;
                if (current.AsDocument.ContainsKey("marker")) sum += current["marker"].AsInt32;
            }
            return sum;
        }
    }
}
