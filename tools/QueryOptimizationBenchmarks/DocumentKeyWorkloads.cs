using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class DocumentKeyWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter == null || !filter.StartsWith("keyparse", StringComparison.Ordinal)) return;
        var rows = db.GetCollection("keyRows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = i }));
        var keys = new[] { "first", "second", "third", "fourth", "with space", "with.dot", "123", "雪" };
        var names = keys.Select(k => JsonSerializer.Serialize(k)).ToArray();
        var document = "{" + string.Join(",", names.Select(n => n + ": Value + @delta")) + "}";
        var select = "SELECT " + document + " FROM keyRows WHERE _id = @id";
        var assignments = string.Join(",", names.Select(n => n + " = Value + @delta"));
        var updateAssignments = "UPDATE keyRows SET " + assignments + " WHERE _id = @id";
        var updateDocument = "UPDATE keyRows SET " + document + " WHERE _id = @id";
        measure("keyparse-select-stream", 2000, i => Select(i, true));
        measure("keyparse-select-cached-control", 2000, i => Select(i, false));
        measure("keyparse-update-assignments", 1000, i => Update(updateAssignments, i));
        measure("keyparse-update-document", 1000, i => Update(updateDocument, i));
        measure("keyparse-point-control", 4000, i => rows.FindById(i % 1024 + 1)["Value"].AsInt32);

        BsonDocument Parameters(int i) => new BsonDocument { ["id"] = i % 1024 + 1, ["delta"] = i % 7 };

        long Select(int i, bool stream)
        {
            using var reader = stream ? db.Execute(new StringReader(select), Parameters(i)) : db.Execute(select, Parameters(i));
            long sum = 0;
            while (reader.Read()) foreach (var key in keys) sum += reader.Current[key].AsInt32;
            return sum;
        }

        long Update(string sql, int i)
        {
            using var reader = db.Execute(sql, Parameters(i));
            long sum = 0;
            while (reader.Read()) sum += reader.Current.AsInt32;
            var result = rows.FindById(i % 1024 + 1);
            foreach (var key in keys) sum += result[key].AsInt32;
            return sum;
        }
    }
}
