using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class CollationWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        // All names have the same ASCII casing; old binary and new database
        // collation agree on these results. Incorrect baselines are tested separately.
        measure("collation-string-scan", 5, i => rows.Query().Where("Name > 'Person5000'").ToList().Sum(x => x.Id));
        measure("collation-numeric-scan", 5, i => rows.Query().Where("Score + 0 > 10000").ToList().Sum(x => x.Id));
        measure("collation-residual-string-range", 2000, i => rows.Query().Where("City = 'City234' AND Name > 'Person5000'")
            .ToList().Sum(x => x.Id));
        measure("collation-indexed-count-control", 20, i => rows.Count(x => x.Score >= 10000));
        measure("collation-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
    }
}
