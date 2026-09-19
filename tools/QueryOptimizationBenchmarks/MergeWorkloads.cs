using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class MergeWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"merge".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("merge", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("merge_rows");
        var prefix = "Shared document title " + new string('x', 470);
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row
        {
            Id = i, Name = prefix + (i * 7919 % 20000).ToString("D5"), Score = i % 97, City = "Group" + i % 17
        }));
        measure("merge-wide-full", 2, i => Checksum(rows.Query().OrderBy(x => x.Name).ToList()));
        measure("merge-wide-mixed", 2, i => Checksum(rows.Query().OrderBy(x => x.Score).ThenByDescending(x => x.Name).ToList()));
        measure("merge-wide-page", 2, i => Checksum(rows.Query().OrderBy(x => x.Name).Offset(5000).Limit(2000).ToList()));
        measure("merge-repeated-keys-control", 2, i => Checksum(rows.Query().OrderBy(x => x.Name.Substring(0, 492)).ToList()));
        var ordinary = db.GetCollection<Program.Row>("rows");
        measure("merge-single-container-control", 2, i => Checksum(ordinary.Query().OrderBy(x => x.Name).ToList()));
        measure("merge-topn-control", 2, i => Checksum(rows.Query().OrderBy(x => x.Name).Limit(10).ToList()));
        plans["merge-full"] = rows.Query().OrderBy(x => x.Name).GetPlan().ToString();
        plans["merge-mixed"] = rows.Query().OrderBy(x => x.Score).ThenByDescending(x => x.Name).GetPlan().ToString();
        plans["merge-page"] = rows.Query().OrderBy(x => x.Name).Offset(5000).Limit(2000).GetPlan().ToString();
    }

    private static long Checksum(IEnumerable<Program.Row> rows)
    {
        long result = 0;
        foreach (var row in rows) result = unchecked(result * 31 + row.Id);
        return result;
    }
}
