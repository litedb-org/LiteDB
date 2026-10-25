using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class TopNWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("topn-single-linq", 5, i => rows.Query().OrderBy(x => x.Name).Limit(10).ToList().Sum(x => x.Id));
        measure("topn-mixed-linq", 5, i => rows.Query().OrderBy(x => x.City).ThenByDescending(x => x.Score)
            .Offset(10).Limit(10).ToList().Sum(x => x.Id));
        measure("topn-mixed-sql", 5, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows ORDER BY City, Score DESC LIMIT 10 OFFSET 10");
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("topn-index-only", 5, i => rows.Query().OrderBy("_id % 3").ThenByDescending("_id")
            .Select("{ id: _id }").Limit(10).ToDocuments().Sum(x => x["id"].AsInt64));
        measure("topn-indexed-control", 2000, i => rows.Query().OrderBy(x => x.Score).Limit(10).ToList().Sum(x => x.Id));
        measure("topn-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["topn"] = rows.Query().OrderBy(x => x.City).ThenByDescending(x => x.Score).Offset(10).Limit(10).GetPlan().ToString();
    }
}
