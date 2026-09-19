using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class MetadataWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("metadata-id-linq", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("metadata-id-sql", 4000, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE _id = 1234");
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("metadata-combined-linq", 4000, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000)
            .FirstOrDefault().Id);
        measure("metadata-projection-linq", 2000, i => rows.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        measure("metadata-count-linq", 4000, i => rows.Query().Where(x => x.Id == 1234).Count());
        measure("metadata-exists-linq", 4000, i => rows.Query().Where(x => x.Score >= 10000).Exists() ? 1 : 0);
        var updated = new Program.Row { Id = 1234, Score = 1234, City = "City234", Name = "Person1234" };
        measure("metadata-update-control", 1000, i => rows.Update(updated) ? 1 : 0);
        measure("metadata-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1"))
            .ToList().Sum(x => x.Id));
        plans["metadata"] = rows.Query().Where(x => x.Id == 1234).GetPlan().ToString();
    }
}
