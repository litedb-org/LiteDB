using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class LikeCharacterWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !filter.StartsWith("likechars", StringComparison.Ordinal) &&
            !"likechars".StartsWith(filter, StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        // Use suffixes whose legacy results also agree with ordinary string matching.
        // Repeated trailing pattern characters expose a separate existing LIKE bug.
        var suffixIds = Enumerable.Range(1, 20000).Where(i => ("Person" + i).EndsWith("2345", StringComparison.Ordinal)).ToArray();
        if (!rows.Find(x => x.Name.EndsWith("2345")).Select(x => x.Id).OrderBy(x => x).SequenceEqual(suffixIds))
            throw new InvalidOperationException("Suffix query does not match the fixture's expected rows.");
        measure("likechars-contains-linq", 5, i => rows.Query().Where(x => x.Name.Contains("123")).ToList().Sum(x => x.Id));
        measure("likechars-prefix-linq", 5, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        measure("likechars-suffix-linq", 5, i => rows.Query().Where(x => x.Name.EndsWith("2345")).ToList().Sum(x => x.Id));
        measure("likechars-contains-sql", 5, i => Read("SELECT _id FROM rows WHERE Name LIKE @pattern", "%123%"));
        measure("likechars-projected-sql", 5, i => Read("SELECT { _id: _id, matched: Name LIKE @pattern } FROM rows", "%123%"));
        measure("likechars-residual-linq", 1000, i => rows.Query().Where(x => x.City == "City234" && x.Name.Contains("123"))
            .ToList().Sum(x => x.Id));
        measure("likechars-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("likechars-numeric-scan-control", 5, i => rows.Query().Where("Score + 0 > 10000").ToList().Sum(x => x.Id));
        plans["likechars-scan"] = rows.Query().Where(x => x.Name.Contains("123")).GetPlan().ToString();
        plans["likechars-residual"] = rows.Query().Where(x => x.City == "City234" && x.Name.Contains("123")).GetPlan().ToString();

        rows.EnsureIndex(x => x.Name);
        try
        {
            if (!rows.Find("Name LIKE 'Person1%3456'").Select(x => x.Id).SequenceEqual(new[] { 13456 }))
                throw new InvalidOperationException("Indexed suffix query does not match the fixture's expected row.");
            measure("likechars-full-index-count", 5, i => rows.Count(x => x.Name.Contains("123")));
            measure("likechars-prefix-remainder-index", 5, i => rows.Query().Where("Name LIKE 'Person1%3456'").ToList().Sum(x => x.Id));
            measure("likechars-prefix-index-control", 1000, i => rows.Query().Where(x => x.Name.StartsWith("Person123"))
                .ToList().Sum(x => x.Id));
            plans["likechars-full-index"] = rows.Query().Where(x => x.Name.Contains("123")).GetPlan().ToString();
            plans["likechars-prefix-remainder"] = rows.Query().Where("Name LIKE 'Person1%3456'").GetPlan().ToString();
        }
        finally
        {
            rows.DropIndex("Name");
        }

        foreach (var culture in new[] { "en-US/IgnoreCase, IgnoreNonSpace", "en-US/Ordinal" })
        {
            using var cultural = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(culture) });
            var names = cultural.GetCollection<Program.Row>("names");
            var labels = new[] { "Café", "Élan", "cafe\u0301", "PLAIN" };
            names.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row { Id = i, Name = labels[i % 4] + i }));
            var suffix = culture.EndsWith("Ordinal", StringComparison.Ordinal) ? "ordinal" : "linguistic";
            measure("likechars-unicode-" + suffix, 5, i => names.Query().Where(x => x.Name.Contains("e")).ToList().Sum(x => x.Id));
        }

        long Read(string sql, string pattern)
        {
            using var reader = db.Execute(sql, new BsonDocument { ["pattern"] = pattern });
            long sum = 0;
            while (reader.Read())
            {
                sum += reader.Current["_id"].AsInt32;
                var matched = reader.Current["matched"];
                if (matched.IsBoolean && matched.AsBoolean) sum += 1000000;
            }
            return sum;
        }
    }
}
