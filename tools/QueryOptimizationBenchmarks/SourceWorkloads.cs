using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class SourceWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("source-filter-scan", 5, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        measure("source-computed-projection", 5, i => rows.Query().Select(x => new { Value = x.Score + 1 }).ToList().Sum(x => x.Value));
        measure("source-computed-topn", 5, i => rows.Query().OrderBy("Score % 17").ThenByDescending(x => x.Id)
            .Limit(10).ToList().Sum(x => x.Id));
        measure("source-residual-count", 10, i => rows.Query().Where("Score % 2 = 0").Count());
        measure("source-indexed-count-control", 20, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("source-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["source"] = rows.Query().Where("Score % 2 = 0").GetPlan().ToString();
    }
}
