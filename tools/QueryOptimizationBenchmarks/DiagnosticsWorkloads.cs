using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class DiagnosticsWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("diagnostics-id-linq", 1000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("diagnostics-id-sql", 1000, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE _id = 1234");
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("diagnostics-combined-linq", 500, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000)
            .FirstOrDefault().Id);
        measure("diagnostics-projection-linq", 500, i => rows.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        measure("diagnostics-explain-control", 1000, i => rows.Query().Where(x => x.Id == 1234).GetPlan()["index"]["cost"].AsInt32);
        measure("diagnostics-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1"))
            .ToList().Sum(x => x.Id));
        plans["diagnostics"] = rows.Query().Where(x => x.Id == 1234).GetPlan().ToString();
    }
}
