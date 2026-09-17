using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class BooleanMembershipWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"boolprefilter".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("boolprefilter", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        var small = Enumerable.Range(1, 1000).ToArray();
        var large = Enumerable.Range(1, 10000).ToArray();
        var even = Enumerable.Range(1, 5000).Select(i => i * 2).ToArray();
        var parameters = new BsonDocument { ["keys"] = new BsonArray(large.Select(i => new BsonValue(i))), ["low"] = 9990 };
        measure("boolprefilter-nested-1000", 100, i => rows.Query().Where(x =>
            (small.Contains(x.Score) && (x.Score >= 990 || x.Score == 2)) || (x.Score >= 15000 && x.Score < 15010))
            .ToList().Sum(x => x.Id));
        measure("boolprefilter-separate-1000", 100, i => rows.Query().Where(x => small.Contains(x.Score))
            .Where(x => x.Score >= 990 || x.Score == 2).ToList().Sum(x => x.Id));
        measure("boolprefilter-parent-10000", 100, i =>
        {
            var low = 9990 + i % 10;
            return rows.Query().Where(x => x.Score >= low && x.Score <= 10000 &&
                (large.Contains(x.Score) || x.Score == 17890)).ToList().Sum(x => x.Id);
        });
        measure("boolprefilter-siblings-10000", 100, i => rows.Query().Where(x =>
            (large.Contains(x.Score) || x.Score == 17890) && (x.Score >= 9990 || x.Score == 2)).ToList().Sum(x => x.Id));
        measure("boolprefilter-two-sets", 100, i => rows.Query().Where(x => large.Contains(x.Score))
            .Where(x => even.Contains(x.Score)).Where(x => x.Score < 4 || x.Score >= 9997).ToList().Sum(x => x.Id));
        const string parent = "Score >= @low AND Score <= 10000 AND (Score IN @keys OR Score = 17890)";
        measure("boolprefilter-sql-parent", 100, i =>
        {
            parameters["low"] = 9990 + i % 10;
            using var reader = db.Execute("SELECT $ FROM rows WHERE " + parent, parameters);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("boolprefilter-sql-empty", 100, i =>
        {
            using var reader = db.Execute("SELECT { n: COUNT(*) } FROM rows WHERE " +
                "(Score IN @keys OR Score = 17890) AND (Score > 10000 OR Score < 0) AND Score >= 0 AND Score <= 10000", parameters);
            if (!reader.Read() || reader.Current["n"].AsInt32 != 0) throw new InvalidOperationException("Expected an empty aggregate");
            return reader.Current["n"].AsInt32;
        });
        measure("boolprefilter-broad-control", 100, i => rows.Query().Where(x =>
            (small.Contains(x.Score) && (x.Score < 15000 || x.Score >= 15000)) || x.Score == 17890).Count());
        measure("boolprefilter-broad-sibling-page-control", 100, i => rows.Query().Where(x =>
            (large.Contains(x.Score) || x.Score == 17890) && (large.Contains(x.Score) || x.Score == 17891))
            .Limit(1).ToList().Sum(x => x.Id));
        measure("boolprefilter-flat-control", 100, i => rows.Query().Where(x =>
            (small.Contains(x.Score) && x.Score >= 990) || (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id));
        measure("boolprefilter-cheaper-id-control", 2000, i => rows.Query().Where(x => x.Id == 1234 &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)).ToList().Sum(x => x.Id));
        measure("boolprefilter-point-control", 4000, i => rows.FindById(1234).Id);
        plans["boolprefilter-parent"] = rows.Query().Where(BsonExpression.Create(parent, parameters)).GetPlan().ToString();
        plans["boolprefilter-siblings"] = rows.Query().Where(x =>
            (large.Contains(x.Score) || x.Score == 17890) && (x.Score >= 9990 || x.Score == 2)).GetPlan().ToString();
    }
}
