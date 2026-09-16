using System;
using System.Collections.Generic;
using LiteDB;

internal static class HelperWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("helper-count-linq", 4000, i => rows.Query().Where(x => x.Id == 1234).Count());
        measure("helper-long-count-linq", 4000, i => rows.Query().Where(x => x.Id == 1234).LongCount());
        measure("helper-exists-linq", 4000, i => rows.Query().Where(x => x.Score >= 10000).Exists() ? 1 : 0);
        measure("helper-empty-exists-linq", 4000, i => rows.Query().Where(x => x.Score > 20000).Exists() ? 1 : 0);
        measure("helper-residual-count-linq", 4000, i => rows.Query().Where(x => x.Id == 1234 && x.Name.StartsWith("Person")).Count());
        measure("helper-sql-control", 4000, i =>
        {
            using var reader = db.Execute("SELECT COUNT(*) AS n FROM rows WHERE _id = 1234");
            reader.Read();
            return reader.Current["n"].AsInt32;
        });
        measure("helper-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["helper"] = rows.Query().Where(x => x.Id == 1234).Select("{ count: COUNT(*._id) }").GetPlan().ToString();
    }
}
