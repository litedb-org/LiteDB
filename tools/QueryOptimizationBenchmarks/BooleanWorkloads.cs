using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class BooleanWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var keys = Enumerable.Range(1, 1000).ToArray();
        measure("boolean-contains-range-linq", 2, i => rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990)
            .ToList().Sum(x => x.Id));
        measure("boolean-wrapped-or-sql", 20, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE (Score = 1234 OR Score = 17890) = true");
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("boolean-control-id", 1000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("boolean-control-combined", 500, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000)
            .FirstOrDefault().Id);
        plans["boolean"] = rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990).GetPlan().ToString();
    }
}
