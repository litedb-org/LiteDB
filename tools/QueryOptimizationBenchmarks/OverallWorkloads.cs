using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class OverallWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var keys = Enumerable.Range(1, 1000).ToArray();
        var enabled = true;
        measure("overall-ordinary-id", 4000, i =>
        {
            var id = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
        });
        measure("overall-ordinary-combined", 2000, i =>
        {
            var city = "City" + i % 1000;
            var minimum = i % 500;
            return rows.Query().Where(x => x.City == city && x.Score >= minimum).FirstOrDefault()?.Id ?? 0;
        });
        measure("overall-ordinary-projection", 2000, i =>
        {
            var city = "City" + i % 1000;
            return rows.Query().Where(x => x.City == city).Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id);
        });
        measure("overall-sql-id", 4000, i => Read("SELECT $ FROM rows WHERE _id = @id", new BsonDocument { ["id"] = i % 20000 + 1 }));
        measure("overall-or-linq", 10, i => rows.Query().Where(x => x.Score == 1234 || x.Score == 17890).ToList().Sum(x => x.Id));
        measure("overall-range-linq", 10, i => rows.Query().Where(x => x.Score >= 10000 && x.Score < 10010).ToList().Sum(x => x.Id));
        measure("overall-contradiction-linq", 10, i => rows.Query().Where(x => x.Score > 10010 && x.Score < 10000).ToList().Sum(x => x.Id));
        measure("overall-guard-linq", 10, i => rows.Query().Where(x => !enabled || x.Score == 1234).ToList().Sum(x => x.Id));
        measure("overall-contains-range-linq", 1, i => rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990).ToList().Sum(x => x.Id));
        measure("overall-common-or-linq", 10, i => rows.Query().Where(x =>
            (x.City == "City234" && x.Name.StartsWith("Person1")) || (x.City == "City234" && x.Score == 1234)).ToList().Sum(x => x.Id));
        measure("overall-secondary-range-count", 10, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("overall-primary-full-count", 10, i => rows.Query().Count());
        measure("overall-exists-linq", 4000, i => rows.Query().Where(x => x.Score >= 10000).Exists() ? 1 : 0);
        measure("overall-topn-single", 3, i => rows.Query().OrderBy(x => x.Name).Limit(10).ToList().Sum(x => x.Id));
        measure("overall-topn-mixed", 3, i => rows.Query().OrderBy(x => x.City).ThenByDescending(x => x.Score)
            .Offset(10).Limit(10).ToList().Sum(x => x.Id));
        measure("overall-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        plans["or"] = rows.Query().Where(x => x.Score == 1234 || x.Score == 17890).GetPlan().ToString();
        plans["contains"] = rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990).GetPlan().ToString();
        plans["count"] = rows.Query().Where(x => x.Score >= 10000).Select("{ count: COUNT(*._id) }").GetPlan().ToString();
        plans["ordinary"] = rows.Query().Where(x => x.Id == 1234).GetPlan().ToString();

        long Read(string sql, BsonDocument parameters)
        {
            using var reader = db.Execute(sql, parameters);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
