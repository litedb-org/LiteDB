using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class RangeUnionWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"union".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("union", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        measure("union-narrow-linq", 20, i =>
        {
            var start = 1000 + i;
            return rows.Query().Where(x => (x.Score >= start && x.Score < start + 10) ||
                (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id);
        });
        measure("union-narrow-sql", 20, i =>
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE " +
                "(Score >= @start AND Score < @end) OR (Score >= 15000 AND Score < 15010)",
                new BsonDocument { ["start"] = 1000 + i, ["end"] = 1010 + i });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("union-descending-page", 20, i => rows.Query().Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010))
            .OrderByDescending(x => x.Score).Offset(8).Limit(5).ToList().Sum(x => x.Id));
        measure("union-overlap-count", 20, i => rows.Count(x =>
            (x.Score >= 1000 && x.Score < 1050) || (x.Score > 1010 && x.Score <= 1060)));
        measure("union-index-projection", 20, i => rows.Query().Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010))
            .Select(x => x.Score).ToList().Sum());
        measure("union-point-and-range", 20, i => rows.Query().Where(x => x.Score == 17890 ||
            (x.Score >= 1000 && x.Score < 1010)).ToList().Sum(x => x.Id));
        measure("union-broad-count", 20, i => rows.Count(x => x.Score < 9000 || x.Score >= 11000));
        measure("union-equality-control", 2000, i => rows.Query().Where(x => x.Score == 1234 || x.Score == 17890)
            .ToList().Sum(x => x.Id));
        measure("union-range-control", 2000, i => rows.Query().Where(x => x.Score >= 1000 && x.Score < 1010)
            .ToList().Sum(x => x.Id));
        measure("union-id-control", 4000, i => rows.FindById(1234).Id);
        measure("union-unindexed-control", 3, i => rows.Query().Where(x =>
            x.Name == "Person1234" || x.Name == "Person17890").ToList().Sum(x => x.Id));
        plans["union-narrow"] = rows.Query().Where(x => (x.Score >= 1000 && x.Score < 1010) ||
            (x.Score >= 15000 && x.Score < 15010)).GetPlan().ToString();
        plans["union-descending"] = rows.Query().Where(x => (x.Score >= 1000 && x.Score < 1010) ||
            (x.Score >= 15000 && x.Score < 15010)).OrderByDescending(x => x.Score).GetPlan().ToString();
        plans["union-overlap"] = rows.Query().Where(x => (x.Score >= 1000 && x.Score < 1050) ||
            (x.Score > 1010 && x.Score <= 1060)).GetPlan().ToString();
    }
}
