using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class AggregateWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("aggregate-range-count-linq", 10, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("aggregate-range-count-sql", 10, i =>
        {
            using var reader = db.Execute("SELECT COUNT(*) AS n FROM rows WHERE Score >= 10000");
            return reader.ToEnumerable().Single()["n"].AsInt32;
        });
        measure("aggregate-full-count-linq", 10, i => rows.Query().Count());
        measure("aggregate-multiple-sql", 10, i =>
        {
            using var reader = db.Execute("SELECT COUNT(*) AS n, COUNT(*._id) AS n2, ANY(*) AS found FROM rows WHERE Score >= 10000");
            var result = reader.ToEnumerable().Single();
            return result["n"].AsInt32 + result["n2"].AsInt32 + (result["found"].AsBoolean ? 1 : 0);
        });
        measure("aggregate-exists-linq", 2000, i => rows.Query().Where(x => x.Score >= 10000).Exists() ? 1 : 0);
        measure("aggregate-empty-exists-linq", 2000, i => rows.Query().Where(x => x.Score > 20000).Exists() ? 1 : 0);
        measure("aggregate-residual-control", 10, i => rows.Query().Where(x => x.Score >= 10000 && x.Name.StartsWith("Person1")).Count());
        measure("aggregate-id-control", 2000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["aggregate"] = rows.Query().Where(x => x.Score >= 10000).Select("{ n: COUNT(*), found: ANY(*._id) }").GetPlan().ToString();
    }
}
