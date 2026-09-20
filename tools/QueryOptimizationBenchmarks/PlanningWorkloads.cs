using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class PlanningWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("planning-id-linq", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("planning-combined-linq", 4000, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000).FirstOrDefault().Id);
        measure("planning-id-sql", 4000, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE 1234 = _id");
            reader.Read();
            return reader.Current["_id"].AsInt32;
        });
        measure("planning-index-order", 2000, i => rows.Query().OrderBy(x => x.Score).Limit(10).ToList().Sum(x => x.Id));
        measure("planning-primary-count", 20, i => rows.Query().Count());
        measure("planning-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        plans["planning"] = rows.Query().OrderBy(x => x.Score).Limit(10).GetPlan().ToString();
    }
}
