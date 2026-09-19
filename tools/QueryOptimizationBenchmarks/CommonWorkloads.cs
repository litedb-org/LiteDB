using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class CommonWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var city = "City234";
        measure("common-or-linq", 10, i => rows.Query().Where(x =>
            (x.City == city && x.Name.StartsWith("Person1")) || (x.City == city && x.Score == 1234)).ToList().Sum(x => x.Id));
        measure("common-or-sql", 10, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE (City = @a AND Name LIKE 'Person1%') OR (City = @b AND Score = 1234)",
                new BsonDocument { ["a"] = city, ["b"] = city });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("common-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("common-combined-control", 4000, i => rows.Query().Where(x => x.City == city && x.Score >= 1000).FirstOrDefault().Id);
        plans["common"] = rows.Query().Where(x =>
            (x.City == city && x.Name.StartsWith("Person1")) || (x.City == city && x.Score == 1234)).GetPlan().ToString();
    }
}
