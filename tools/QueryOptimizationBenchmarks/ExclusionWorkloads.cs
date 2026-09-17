using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class ExclusionWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"exclusion".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("exclusion", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("exclusions");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row
        {
            Id = i, Score = i % 1000 == 0 ? (i % 3 == 0 ? -1 : 1) : 0,
            City = "City" + i % 1000, Name = "Person" + i
        }));
        rows.EnsureIndex(x => x.Score);
        measure("exclusion-rare-results-linq", 100, i => rows.Query().Where(x => x.Score != 0).ToList().Sum(x => x.Id));
        measure("exclusion-rare-results-sql", 100, i => Read("SELECT $ FROM exclusions WHERE Score != 0"));
        measure("exclusion-rare-results-count", 100, i => rows.Count(x => x.Score != 0));
        measure("exclusion-rare-results-descending", 100, i => rows.Query().Where(x => x.Score != 0)
            .OrderByDescending(x => x.Score).ToList().Sum(x => x.Id));
        measure("exclusion-most-results-control", 20, i => rows.Count(x => x.Score != -1));
        measure("exclusion-all-results-control", 20, i => rows.Count(x => x.Score != 2));
        var ordinary = db.GetCollection<Program.Row>("rows");
        measure("exclusion-id-control", 4000, i => ordinary.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["exclusion"] = rows.Query().Where(x => x.Score != 0).GetPlan().ToString();
        plans["exclusion-count"] = rows.Query().Where(x => x.Score != 0).Select("COUNT(*)").GetPlan().ToString();
        plans["exclusion-descending"] = rows.Query().Where(x => x.Score != 0).OrderByDescending(x => x.Score).GetPlan().ToString();

        long Read(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
