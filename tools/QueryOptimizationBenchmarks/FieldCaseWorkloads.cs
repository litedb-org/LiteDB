using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class FieldCaseWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var documents = db.GetCollection("rows");
        measure("fieldcase-point-sql", 20, i => ReadSql("SELECT $ FROM rows WHERE score = 1234"));
        measure("fieldcase-point-linq", 20, i => documents.Query().Where(x => x["score"] == 1234).ToList().Sum(x => x["_id"].AsInt32));
        measure("fieldcase-bounded-range", 20, i => rows.Query().Where("score >= 10000 AND ScOrE < 10010").ToList().Sum(x => x.Id));
        measure("fieldcase-disjunction", 20, i => rows.Query().Where("score = 1234 OR SCORE = 17890").ToList().Sum(x => x.Id));
        measure("fieldcase-common-guard", 20, i => rows.Query().Where("(city = 'City234' AND Score > 10000) OR (CITY = 'City234' AND Name = 'Person1234')")
            .ToList().Sum(x => x.Id));
        measure("fieldcase-covered-order", 5, i => rows.Query().OrderBy("score", Query.Descending).Select("{ value: SCORE }")
            .ToList().Sum(x => x["value"].AsInt32));
        measure("fieldcase-group-count", 3, i => rows.Query().GroupBy("city").Select("{ key: @key, n: COUNT(*) }")
            .ToList().Sum(x => x["n"].AsInt32));
        measure("fieldcase-exact-id-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("fieldcase-exact-projection-control", 2000, i => rows.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        long ReadSql(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }

        plans["fieldcase-point"] = rows.Query().Where("score = 1234").GetPlan().ToString();
        plans["fieldcase-range"] = rows.Query().Where("score >= 10000 AND ScOrE < 10010").GetPlan().ToString();
        plans["fieldcase-or"] = rows.Query().Where("score = 1234 OR SCORE = 17890").GetPlan().ToString();
        plans["fieldcase-order"] = rows.Query().OrderBy("score", Query.Descending).Select("{ value: SCORE }").GetPlan().ToString();
        plans["fieldcase-group"] = rows.Query().GroupBy("city").Select("{ key: @key, n: COUNT(*) }").GetPlan().ToString();
    }
}
