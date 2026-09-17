using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class EscapedFieldWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !"escaped".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("escaped", StringComparison.Ordinal)) return;
        var rows = db.GetCollection("escaped_rows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new BsonDocument
        {
            ["_id"] = i, ["Score.Value"] = i, ["Payload"] = new string('x', 200)
        }));
        rows.EnsureIndex("literal", x => x["Score.Value"]);
        measure("escaped-covered-projection", 5, i => rows.Query().Select(x => new { Value = x["Score.Value"] })
            .ToList().Sum(x => x.Value.AsInt32));
        measure("escaped-covered-filtered", 10, i => rows.Query().Where(x => x["Score.Value"] >= 10000)
            .Select(x => new { Value = x["Score.Value"] }).ToList().Sum(x => x.Value.AsInt32));
        measure("escaped-literal-count", 20, i => rows.Query().Select("{ n: COUNT(*.@.[\"Score.Value\"]) }").ToList().Single()["n"].AsInt32);
        var ordinary = db.GetCollection<Program.Row>("rows");
        measure("escaped-ordinary-preferred-count", 20, i => ordinary.Query().Select("{ n: COUNT(*.Score) }").ToList().Single()["n"].AsInt32);
        measure("escaped-ordinary-preferred-projection", 5, i => ordinary.Query().Select(x => x.Score).ToList().Sum());
        measure("escaped-ordinary-projection-control", 2000, i => ordinary.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        measure("escaped-id-control", 4000, i => ordinary.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        plans["escaped-projection"] = rows.Query().Select(x => new { Value = x["Score.Value"] }).GetPlan().ToString();
        plans["escaped-filtered"] = rows.Query().Where(x => x["Score.Value"] >= 10000).Select(x => new { Value = x["Score.Value"] }).GetPlan().ToString();
        plans["escaped-count"] = rows.Query().Select("{ n: COUNT(*.@.[\"Score.Value\"]) }").GetPlan().ToString();
    }
}
