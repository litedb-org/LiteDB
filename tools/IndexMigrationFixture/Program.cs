using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LiteDB;

// Run with the destination ZIP path. These are actual released-writer files;
// neither their indexes nor their reserved header bytes are patched afterward.
internal static class Program
{
    private static void Main(string[] args)
    {
        const string flags = "MAP($.values[*] => IIF(@ = DOUBLE(@), UPPER($.payload), $.payload))";
        var probe = new BsonDocument { ["values"] = new BsonArray { 0.1m, 0.1d }, ["payload"] = "lower" };
        if (BsonExpression.Create(flags).Execute(probe).Distinct().Count() != 1)
            throw new Exception("Released multikey expression no longer omits the second key");
        using var output = ZipFile.Open(args[0], ZipArchiveMode.Create);
        foreach (var password in new[] { null, "migration-power-loss" })
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
                { DataStream = data, LogStream = log, Password = password, Collation = Collation.Binary })))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 48).Select(i => new BsonDocument
                {
                    ["_id"] = i,
                    ["oid"] = new ObjectId((i % 2 == 0 ? "80000000" : "7fffffff") + "1122334455" + i.ToString("x6")),
                    ["values"] = new BsonArray { 0.1m, 0.1d },
                    ["number"] = 0.1m,
                    ["payload"] = new string((char)('a' + i % 26), 240) + i
                }));
                rows.EnsureIndex("oid");
                rows.EnsureIndex("values", "$.values[*]");
                rows.EnsureIndex("flags", flags);
                rows.EnsureIndex("computed", "IIF($.number = DOUBLE($.number), 1, 0)");
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
                db.Checkpoint();
                db.LimitSize = 1024 * 1024;
                db.Checkpoint();
                // Guards prove the fixtures actually distinguish the old comparer.
                if (rows.Count("IIF($.number = DOUBLE($.number), 1, 0) = 1") != 48 ||
                    rows.Query().OrderBy("$.oid").First()["_id"].AsInt32 % 2 != 0)
                    throw new Exception("Fixture did not reach legacy comparison behavior");
            }
            var name = password == null ? "plain" : "encrypted";
            using (var target = output.CreateEntry(name + ".db", CompressionLevel.Optimal).Open()) data.WriteTo(target);
            // Keep the released writer's initialized encrypted WAL preamble.
            // The fault matrix targets migration, not first-time salt creation.
            using (var target = output.CreateEntry(name + "-log.db", CompressionLevel.Optimal).Open()) log.WriteTo(target);
        }
    }
}
