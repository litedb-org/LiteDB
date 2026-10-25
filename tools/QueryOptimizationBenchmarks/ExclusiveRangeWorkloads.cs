using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class ExclusiveRangeWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"exclusive".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("exclusive", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("exclusive_ranges");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row
        {
            Id = i, Score = i % 1000 == 0 ? (i % 3 == 0 ? -1 : 1) : 0,
            City = "City" + i % 1000, Name = "Person" + i
        }));
        rows.EnsureIndex(x => x.Score);
        measure("exclusive-greater-linq", 100, i => rows.Query().Where(x => x.Score > 0).ToList().Sum(x => x.Id));
        measure("exclusive-bounded-sql", 100, i => Read("SELECT $ FROM exclusive_ranges WHERE Score > 0 AND Score < 2"));
        measure("exclusive-greater-count", 100, i => rows.Count(x => x.Score > 0));
        measure("exclusive-less-descending", 100, i => rows.Query().Where(x => x.Score < 0)
            .OrderByDescending(x => x.Score).ToList().Sum(x => x.Id));
        measure("exclusive-inclusive-control", 20, i => rows.Count(x => x.Score >= 0));
        measure("exclusive-end-bound-control", 1000, i => rows.Count(x => x.Score < 0));
        var ordinary = db.GetCollection<Program.Row>("rows");
        measure("exclusive-unique-keys-control", 2000, i => ordinary.Query().Where(x => x.Score > 1234 && x.Score < 1240).ToList().Sum(x => x.Id));
        measure("exclusive-id-control", 4000, i => ordinary.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["exclusive-range"] = rows.Query().Where(x => x.Score > 0).GetPlan().ToString();
        plans["exclusive-descending"] = rows.Query().Where(x => x.Score < 0).OrderByDescending(x => x.Score).GetPlan().ToString();

        long Read(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
