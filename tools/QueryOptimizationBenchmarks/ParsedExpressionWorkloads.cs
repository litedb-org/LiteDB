using System;
using System.Linq;
using LiteDB;

internal static class ParsedExpressionWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter != null && !filter.StartsWith("textir", StringComparison.Ordinal) &&
            !"textir".StartsWith(filter, StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        const string point = "_id = @id";
        measure("textir-find-by-id", 4000, i => rows.FindById(i % 1024 + 1).Id);
        measure("textir-point-named", 4000, i => rows.Query().Where(point, Id(i)).FirstOrDefault().Id);
        measure("textir-point-positional", 4000, i => rows.Query().Where("_id = @0", i % 1024 + 1).FirstOrDefault().Id);
        measure("textir-combined", 1000, i => rows.Query().Where("City = @city AND Score >= @minimum",
            new BsonDocument { ["city"] = "City" + i % 1000, ["minimum"] = i % 500 }).Limit(5).ToList().Sum(x => (long)x.Id));
        measure("textir-bounded-range", 1000, i => rows.Query().Where("Score >= @low AND Score < @high",
            new BsonDocument { ["low"] = 10000 + i % 1024, ["high"] = 10010 + i % 1024 }).ToList().Sum(x => (long)x.Id));
        measure("textir-projection", 1000, i => rows.Query().Where("City = @city", new BsonDocument { ["city"] = "City" + i % 1000 })
            .OrderBy("_id", Query.Descending).Select(BsonExpression.Create(
                "{ _id: _id, label: Name + @suffix, flag: Score >= @minimum }",
                new BsonDocument { ["suffix"] = "!" + i % 3, ["minimum"] = i % 1000 })).Limit(5).ToList()
            .Sum(x => x["_id"].AsInt32 + x["label"].AsString.Length * 10000L + (x["flag"].AsBoolean ? 1000000L : 0)));
        measure("textir-nested-projection", 1000, i => rows.Query().Where(point, Id(i)).Select(BsonExpression.Create(
            "{ _id: _id, values: ARRAY(MAP([1,2,3] => @ + @delta)) }", new BsonDocument { ["delta"] = i % 1024 }))
            .ToList().Sum(x => x["_id"].AsInt32 + x["values"].AsArray.Sum(v => (long)v.AsInt32)));
        measure("textir-count", 1000, i => rows.Count(BsonExpression.Create("Score >= @low AND Score < @high",
            new BsonDocument { ["low"] = 10000, ["high"] = 10100 })));

        // Fixed parsed shape isolates admission/churn from delegate compilation.
        var tagged = Enumerable.Range(0, 256).Select(i => point + " -- query " + i).ToArray();
        measure("textir-64-shapes", 1024, i => rows.Query().Where(tagged[i % 64], Id(i)).FirstOrDefault().Id);
        measure("textir-256-churn-control", 1024, i => rows.Query().Where(tagged[i % 256], Id(i)).FirstOrDefault().Id);
        var literals = Enumerable.Range(1, 256).Select(i => "_id = " + i).ToArray();
        measure("textir-literal-churn-control", 1024, i => rows.Query().Where(literals[i % 256]).FirstOrDefault().Id);
        var longText = point + new string(' ', 8192);
        measure("textir-long-text-control", 1000, i => rows.Query().Where(longText, Id(i)).FirstOrDefault().Id);

        var template = BsonExpression.Create(point);
        measure("textir-prebound-control", 4000, i => rows.FindOne(template.Bind(Id(i))).Id);
        measure("textir-linq-control", 4000, i =>
        {
            var id = i % 1024 + 1;
            return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
        });
        measure("textir-sql-control", 4000, i =>
        {
            using var reader = db.Execute("SELECT _id FROM rows WHERE _id = @id", Id(i));
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });
        measure("textir-scan-control", 3, i => rows.Query().Where("Name LIKE 'Person1%'").ToList().Sum(x => (long)x.Id));
    }

    private static BsonDocument Id(int i) => new BsonDocument { ["id"] = i % 1024 + 1 };
}
