using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class TraversalGuardWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("guard-primary-count", 20, i => rows.Count());
        measure("guard-scalar-field-count", 20, i => rows.Query().Select("{ n: COUNT(*.Score) }").ToList().Single()["n"].AsInt32);
        measure("guard-exclusion-count", 20, i => rows.Count(x => x.Score != 10000));
        measure("guard-indexed-range-count", 20, i => rows.Count(x => x.Score >= 10000));
        measure("guard-ordinary-id", 4000, i =>
        {
            var id = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
        });
        measure("guard-ordinary-projection", 2000, i => rows.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        measure("guard-scan-control", 5, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
    }
}
