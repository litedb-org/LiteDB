using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class BooleanRangeWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"boolrange".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("boolrange", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        var keys = Enumerable.Range(1, 1000).ToArray();
        measure("boolrange-nested-linq", 10, i =>
        {
            var start = 1000 + i;
            return rows.Query().Where(x => (x.Score >= start && x.Score < 15010 &&
                (x.Score < start + 10 || x.Score >= 15000)) || x.Score == 17890).ToList().Sum(x => x.Id);
        });
        const string nested = "(Score BETWEEN 1000 AND 15009 AND (Score < 1010 OR Score >= 15000)) OR Score = 17890";
        measure("boolrange-nested-sql", 10, i => Read(nested));
        measure("boolrange-combined-linq", 10, i => rows.Query().Where(x =>
            (x.Score < 1010 || x.Score >= 15000) && (x.Score >= 1000 || x.Score == 50) && x.Score < 15010).ToList().Sum(x => x.Id));
        measure("boolrange-separate-where", 10, i => rows.Query()
            .Where(x => x.Score >= 1000 && x.Score < 15010).Where(x => x.Score < 1010 || x.Score >= 15000)
            .ToList().Sum(x => x.Id));
        measure("boolrange-nested-membership", 2, i => rows.Query().Where(x =>
            (keys.Contains(x.Score) && (x.Score >= 990 || x.Score == 2)) || (x.Score >= 15000 && x.Score < 15010))
            .ToList().Sum(x => x.Id));
        measure("boolrange-separate-membership", 2, i => rows.Query().Where(x => keys.Contains(x.Score))
            .Where(x => x.Score >= 990 || x.Score == 2).ToList().Sum(x => x.Id));
        measure("boolrange-nested-count", 10, i => rows.Query().Where(nested).Count());
        measure("boolrange-descending-page", 10, i => rows.Query().Where(nested).OrderByDescending(x => x.Score)
            .Offset(8).Limit(5).ToList().Sum(x => x.Id));
        measure("boolrange-contradiction", 10, i => rows.Query().Where(
            "(Score < 1010 OR Score > 15000) AND (Score >= 1010 OR Score < 0) AND Score <= 15000 AND Score >= 0").Count());
        measure("boolrange-range-union-control", 1000, i => rows.Query().Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id));
        measure("boolrange-set-union-control", 100, i => rows.Query().Where(x =>
            (keys.Contains(x.Score) && x.Score >= 990) || (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id));
        measure("boolrange-cheaper-id-control", 2000, i => rows.Query().Where(x => x.Id == 1234 &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)).ToList().Sum(x => x.Id));
        measure("boolrange-point-control", 4000, i => rows.FindById(1234).Id);
        plans["boolrange-nested"] = rows.Query().Where(nested).GetPlan().ToString();
        plans["boolrange-combined"] = rows.Query().Where(x =>
            (x.Score < 1010 || x.Score >= 15000) && (x.Score >= 1000 || x.Score == 50) && x.Score < 15010).GetPlan().ToString();
        plans["boolrange-membership"] = rows.Query().Where(x =>
            (keys.Contains(x.Score) && (x.Score >= 990 || x.Score == 2)) || (x.Score >= 15000 && x.Score < 15010)).GetPlan().ToString();

        long Read(string predicate)
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE " + predicate);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
