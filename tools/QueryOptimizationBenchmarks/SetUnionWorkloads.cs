using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class SetUnionWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"setunion".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("setunion", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        var keys = new[] { 1234, 1234, 15002, 17890 };
        var many = Enumerable.Range(1, 1000).ToArray();
        var parameters = new BsonDocument { ["keys"] = new BsonArray(keys.Select(x => new BsonValue(x))) };
        measure("setunion-contains-range-linq", 10, i =>
        {
            var start = 15000 + i;
            return rows.Query().Where(x => keys.Contains(x.Score) || (x.Score >= start && x.Score < start + 10))
                .ToList().Sum(x => x.Id);
        });
        measure("setunion-in-range-sql", 10, i => Read("(Score IN @keys) OR (Score >= 15000 AND Score < 15010)"));
        measure("setunion-between-sql", 10, i => Read("(Score BETWEEN 1000 AND 1009) OR (Score BETWEEN 15000 AND 15009)"));
        measure("setunion-two-sets-linq", 10, i =>
        {
            var other = new[] { 1234, 4567 + i, 17890 };
            return rows.Query().Where(x => keys.Contains(x.Score) || other.Contains(x.Score)).ToList().Sum(x => x.Id);
        });
        measure("setunion-intersect-linq", 2, i => rows.Query().Where(x =>
            (many.Contains(x.Score) && x.Score >= 990) || (x.Score >= 15000 && x.Score < 15010))
            .ToList().Sum(x => x.Id));
        measure("setunion-covered-count", 10, i => rows.Query().Where(x => keys.Contains(x.Score) ||
            (x.Score >= 15000 && x.Score < 15010)).Count());
        measure("setunion-large-union-count", 2, i => rows.Query().Where(x => many.Contains(x.Score) || x.Score >= 19000).Count());
        measure("setunion-descending-page", 10, i => rows.Query().Where(x => keys.Contains(x.Score) ||
            (x.Score >= 15000 && x.Score < 15010)).OrderByDescending(x => x.Score).Offset(8).Limit(4)
            .ToList().Sum(x => x.Id));
        measure("setunion-existing-range-control", 1000, i => rows.Query().Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id));
        measure("setunion-existing-in-control", 1000, i => rows.Query().Where(x => keys.Contains(x.Score)).ToList().Sum(x => x.Id));
        measure("setunion-cheaper-id-control", 1000, i => rows.Query().Where(x => x.Id == 20 &&
            (many.Contains(x.Score) || x.Score >= 19000)).ToList().Sum(x => x.Id));
        measure("setunion-unindexed-control", 3, i => Read("Name IN ['Person1','Person2'] OR Name = 'Person3'"));
        plans["setunion-contains"] = rows.Query().Where(x => keys.Contains(x.Score) || (x.Score >= 15000 && x.Score < 15010)).GetPlan().ToString();
        plans["setunion-between"] = rows.Query().Where("(Score BETWEEN 1000 AND 1009) OR (Score BETWEEN 15000 AND 15009)").GetPlan().ToString();
        plans["setunion-intersect"] = rows.Query().Where(x => (many.Contains(x.Score) && x.Score >= 990) ||
            (x.Score >= 15000 && x.Score < 15010)).GetPlan().ToString();

        long Read(string predicate)
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE " + predicate, parameters);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
