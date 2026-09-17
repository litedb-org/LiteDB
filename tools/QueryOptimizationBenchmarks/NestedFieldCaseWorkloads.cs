using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class NestedFieldCaseWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !filter.StartsWith("nestedcase", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Row>("nestedRows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Row
        {
            Id = i, Name = "Person" + i,
            Owner = new Owner { Score = i, City = "City" + i % 1000, Manager = new BsonDocument { ["$id"] = 1, ["$ref"] = "managers" } }
        }));
        db.GetCollection("managers").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "Resolved" });
        rows.EnsureIndex("nestedScore", "owner.score");
        rows.EnsureIndex("nestedCity", "owner.city");
        measure("nestedcase-point-sql", 20, i =>
        {
            using var reader = db.Execute("SELECT $ FROM nestedRows WHERE Owner.Score = @score", new BsonDocument { ["score"] = 1234 + i % 100 });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("nestedcase-point-linq", 20, i =>
        {
            var score = 1234 + i % 100;
            return rows.Query().Where(x => x.Owner.Score == score).ToList().Sum(x => x.Id);
        });
        measure("nestedcase-range-linq", 20, i =>
        {
            var low = 10000 + i % 5;
            var high = low + 10;
            return rows.Query().Where(x => x.Owner.Score >= low && x.Owner.Score < high).ToList().Sum(x => x.Id);
        });
        measure("nestedcase-or-linq", 20, i => rows.Query().Where(x => x.Owner.Score == 1234 || x.Owner.Score == 17890).ToList().Sum(x => x.Id));
        const string nested = "(Owner.Score >= 10000 AND (OWNER.SCORE < 10010 OR Owner.score >= 10020)) AND Owner.Score < 10030";
        measure("nestedcase-boolean", 20, i => rows.Query().Where(nested).ToList().Sum(x => x.Id));
        var keys = new[] { 1234, 1235, 17890 };
        measure("nestedcase-membership-linq", 20, i => rows.Query().Where(x => keys.Contains(x.Owner.Score) || x.Owner.Score > 19998).ToList().Sum(x => x.Id));
        measure("nestedcase-count-linq", 20, i => rows.Count(x => x.Owner.Score >= 19990 && x.Owner.Score < 20000));
        measure("nestedcase-exists-linq", 20, i => rows.Exists(x => x.Owner.Score == 19999) ? 1 : 0);
        measure("nestedcase-page", 20, i => rows.Query().OrderBy("Owner.Score", Query.Descending).Select("{ value: OWNER.SCORE }")
            .Offset(10).Limit(20).ToList().Sum(x => x["value"].AsInt32));
        measure("nestedcase-group-count", 5, i => rows.Query().GroupBy("Owner.City").Select("{ key: @key, n: COUNT(*) }")
            .ToList().Sum(x => x["n"].AsInt32));
        measure("nestedcase-exact-nested-control", 4000, i => rows.Query().Where("owner.score = 1234").ToList().Sum(x => x.Id));
        measure("nestedcase-sibling-include-control", 2000, i => rows.Query().Include("Owner.Manager").Where("owner.score = 1234")
            .ToList().Sum(x => x.Owner.Manager["Name"].AsString.Length + x.Id));
        measure("nestedcase-id-control", 4000, i => rows.FindById(1234).Id);
        measure("nestedcase-root-case-control", 4000, i => db.GetCollection<Program.Row>("rows").Query().Where("score = 1234").ToList().Sum(x => x.Id));
        measure("nestedcase-scan-control", 5, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        plans["nestedcase-point"] = rows.Query().Where(x => x.Owner.Score == 1234).GetPlan().ToString();
        plans["nestedcase-range"] = rows.Query().Where(x => x.Owner.Score >= 10000 && x.Owner.Score < 10010).GetPlan().ToString();
        plans["nestedcase-boolean"] = rows.Query().Where(nested).GetPlan().ToString();
        plans["nestedcase-page"] = rows.Query().OrderBy("Owner.Score", Query.Descending).Select("{ value: OWNER.SCORE }").Offset(10).Limit(20).GetPlan().ToString();
        plans["nestedcase-group"] = rows.Query().GroupBy("Owner.City").Select("{ key: @key, n: COUNT(*) }").GetPlan().ToString();
        plans["nestedcase-exact"] = rows.Query().Where("owner.score = 1234").GetPlan().ToString();
        plans["nestedcase-include"] = rows.Query().Include("Owner.Manager").Where("owner.score = 1234").GetPlan().ToString();
    }

    public class Row
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public Owner Owner { get; set; }
    }

    public class Owner
    {
        public int Score { get; set; }
        public string City { get; set; }
        public BsonDocument Manager { get; set; }
    }
}
