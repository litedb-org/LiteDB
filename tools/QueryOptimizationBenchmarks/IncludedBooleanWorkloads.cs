using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class IncludedBooleanWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !filter.StartsWith("includebool", StringComparison.Ordinal)) return;
        db.GetCollection("people").InsertBulk(Enumerable.Range(1, 1000).Select(i => new BsonDocument
        {
            ["_id"] = i, ["Name"] = "Owner" + i, ["Weight"] = i % 10 + 1
        }));
        var rows = db.GetCollection<Row>("includedRows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Row
        {
            Id = i, Score = i, City = "City" + i % 1000, Ref = new Person { Id = i % 1000 + 1 },
            Owner = new Owner { Score = i, Manager = new Person { Id = i % 1000 + 1 } }
        }));
        rows.EnsureIndex("score", "Score");
        rows.EnsureIndex("city", "City");
        rows.EnsureIndex("nested", "Owner.Score");
        rows.EnsureIndex("affected", "Ref.Weight");
        measure("includebool-root-range-linq", 10, i => rows.Query().Include(x => x.Ref).Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010)).ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-sibling-range-linq", 10, i => rows.Query().Include(x => x.Owner.Manager).Where(x =>
            (x.Owner.Score >= 1000 && x.Owner.Score < 1010) || (x.Owner.Score >= 15000 && x.Owner.Score < 15010))
            .ToList().Sum(x => x.Id + x.Owner.Manager.Weight));
        const string nested = "(owner.score BETWEEN @low AND 15009 AND (owner.score < @high OR owner.score >= 15000)) OR owner.score = 17890";
        measure("includebool-nested-sql", 10, i =>
        {
            using var reader = db.Execute("SELECT $ FROM includedRows INCLUDE Owner.Manager, Ref WHERE " + nested,
                new BsonDocument { ["low"] = 1000 + i, ["high"] = 1010 + i });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32 + reader.Current["Owner"]["Manager"]["Weight"].AsInt32 + reader.Current["Ref"]["Weight"].AsInt32;
            return sum;
        });
        measure("includebool-separate-where", 10, i => rows.Query().Include(x => x.Ref)
            .Where(x => x.Score >= 1000 && x.Score < 15010).Where(x => x.Score < 1010 || x.Score >= 15000)
            .ToList().Sum(x => x.Id + x.Ref.Weight));
        var keys = Enumerable.Range(1, 1000).ToArray();
        measure("includebool-separate-membership", 2, i => rows.Query().Include(x => x.Ref)
            .Where(x => keys.Contains(x.Score)).Where(x => x.Score >= 990 || x.Score == 2)
            .ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-common-guard", 10, i => rows.Query().Include(x => x.Ref).Where(x =>
            (x.City == "City234" && x.Ref.Weight >= 5) || (x.City == "City234" && x.Ref.Name == "Owner1"))
            .ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-sibling-common-guard", 10, i => rows.Query().Include(x => x.Owner.Manager).Where(x =>
            (x.Owner.Score == 1234 && x.Owner.Manager.Weight >= 5) || (x.Owner.Score == 1234 && x.Owner.Manager.Name == "Owner1"))
            .ToList().Sum(x => x.Id + x.Owner.Manager.Weight));
        const string range = "(Owner.Score >= 1000 AND Owner.Score < 1010) OR (Owner.Score >= 15000 AND Owner.Score < 15010)";
        measure("includebool-descending-page", 10, i => rows.Query().Include(x => x.Owner.Manager).Where(range)
            .OrderByDescending(x => x.Owner.Score).Offset(8).Limit(5).ToList().Sum(x => x.Id + x.Owner.Manager.Weight));
        measure("includebool-replayed-aggregate", 10, i =>
        {
            using var reader = db.Execute("SELECT { n: COUNT(*), weight: SUM(*.Owner.Manager.Weight), again: SUM(*.Owner.Manager.Weight) } " +
                "FROM includedRows INCLUDE Owner.Manager WHERE " + range);
            reader.Read();
            return reader.Current["n"].AsInt32 + reader.Current["weight"].AsInt32 + reader.Current["again"].AsInt32;
        });
        measure("includebool-broad-count", 5, i => rows.Query().Include(x => x.Ref).Where(x => x.Score < 10001 || x.Score > 12000).Count());
        measure("includebool-affected-control", 1000, i => rows.Query().Include(x => x.Ref).Where(x =>
            (x.Ref.Weight >= 8 && x.Ref.Weight <= 9) || x.Ref.Weight == 2).Limit(10).ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-point-control", 2000, i => rows.Query().Include(x => x.Ref).Where(x => x.Score == 1234).ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-cheaper-id-control", 2000, i => rows.Query().Include(x => x.Ref).Where(x => x.Id == 1234 &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)).ToList().Sum(x => x.Id + x.Ref.Weight));
        measure("includebool-without-include-control", 1000, i => rows.Query().Where(range).ToList().Sum(x => x.Id));
        plans["includebool-root"] = rows.Query().Include(x => x.Ref).Where("(Score >= 1000 AND Score < 1010) OR (Score >= 15000 AND Score < 15010)").GetPlan().ToString();
        plans["includebool-sibling"] = rows.Query().Include(x => x.Owner.Manager).Where(range).GetPlan().ToString();
        plans["includebool-separate"] = rows.Query().Include(x => x.Ref).Where(x => x.Score >= 1000 && x.Score < 15010)
            .Where(x => x.Score < 1010 || x.Score >= 15000).GetPlan().ToString();
        plans["includebool-membership"] = rows.Query().Include(x => x.Ref).Where(x => keys.Contains(x.Score))
            .Where(x => x.Score >= 990 || x.Score == 2).GetPlan().ToString();
        plans["includebool-guard"] = rows.Query().Include(x => x.Ref).Where(x =>
            (x.City == "City234" && x.Ref.Weight >= 5) || (x.City == "City234" && x.Ref.Name == "Owner1")).GetPlan().ToString();
        plans["includebool-page"] = rows.Query().Include(x => x.Owner.Manager).Where(range).OrderByDescending(x => x.Owner.Score).Offset(8).Limit(5).GetPlan().ToString();
        plans["includebool-affected"] = rows.Query().Include(x => x.Ref).Where(x =>
            (x.Ref.Weight >= 8 && x.Ref.Weight <= 9) || x.Ref.Weight == 2).Limit(10).GetPlan().ToString();
        plans["includebool-point"] = rows.Query().Include(x => x.Ref).Where(x => x.Score == 1234).GetPlan().ToString();
        plans["includebool-no-include"] = rows.Query().Where(range).GetPlan().ToString();
    }

    public class Row
    {
        public int Id { get; set; }
        public int Score { get; set; }
        public string City { get; set; }
        [BsonRef("people")]
        public Person Ref { get; set; }
        public Owner Owner { get; set; }
    }

    public class Owner
    {
        public int Score { get; set; }
        [BsonRef("people")]
        public Person Manager { get; set; }
    }

    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Weight { get; set; }
    }
}
