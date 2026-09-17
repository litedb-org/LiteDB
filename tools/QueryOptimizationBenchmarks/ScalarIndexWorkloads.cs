using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class ScalarIndexWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"scalar-index".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("scalar-index", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        var tags = db.GetCollection("tag_rows");
        tags.InsertBulk(Enumerable.Range(1, 20000).Select(i => new BsonDocument
        {
            ["_id"] = i, ["Tags"] = new BsonArray(i % 1000, (i + 1) % 1000)
        }));
        tags.EnsureIndex("tags", "Tags[*]");
        measure("scalar-index-range-count", 20, i => rows.Query().Where(x => x.Score >= 10000).Count());
        measure("scalar-index-full-count", 20, i => rows.Query().Where(x => x.Score >= 1).Count());
        measure("scalar-index-covered-order", 5, i => rows.Query().OrderBy(x => x.Score).Select(x => x.Score).ToList().Sum());
        measure("scalar-index-duplicate-key-count", 2000, i => rows.Query().Where(x => x.City == "City234").Count());
        measure("scalar-index-point-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("scalar-index-multikey-control", 10, i => tags.Query().Where("Tags[*] ANY >= 0").Count());
        measure("scalar-index-scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        plans["scalar-index-count"] = rows.Query().Where(x => x.Score >= 10000).Select("{ n: COUNT(*) }").GetPlan().ToString();
        plans["scalar-index-order"] = rows.Query().OrderBy(x => x.Score).Select(x => x.Score).GetPlan().ToString();
        plans["scalar-index-multikey"] = tags.Query().Where("Tags[*] ANY >= 0").GetPlan().ToString();
    }
}
