using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class PrimaryWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("primary-full-count", 20, i => rows.Query().Count());
        measure("primary-range-count", 20, i => rows.Query().Where(x => x.Id >= 10000).Count());
        measure("primary-range-documents", 10, i => rows.Query().Where(x => x.Id >= 10000).ToList().Sum(x => x.Id));
        measure("primary-point", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("primary-secondary-control", 20, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("primary-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        plans["primary"] = rows.Query().Where(x => x.Id >= 10000).Select("{ count: COUNT(*) }").GetPlan().ToString();
    }
}
