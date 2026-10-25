using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class UniqueWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"unique".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("unique", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("unique_rows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row
        {
            Id = i, Score = i, City = "City" + i % 1000, Name = "Person" + i
        }));
        rows.EnsureIndex(x => x.Score, true);
        rows.EnsureIndex(x => x.City);
        measure("unique-range-count", 20, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("unique-full-count", 20, i => rows.Query().Where(x => x.Score >= 1).Count());
        measure("unique-covered-projection", 5, i => rows.Query().Where(x => x.Score >= 10000).Select(x => x.Score).ToList().Sum());
        measure("unique-document-range", 5, i => rows.Query().Where(x => x.Score >= 10000).ToList().Sum(x => x.Id));
        measure("unique-nonunique-control", 20, i => rows.Query().Where(x => x.City == "City234").Count());
        measure("unique-primary-control", 20, i => rows.Query().Count());
        plans["unique-count"] = rows.Query().Where(x => x.Score >= 10000).Select("{ n: COUNT(*) }").GetPlan().ToString();
        plans["unique-projection"] = rows.Query().Where(x => x.Score >= 10000).Select(x => x.Score).GetPlan().ToString();
    }
}
