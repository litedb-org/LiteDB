using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main()
    {
        try
        {
            ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
            // All inputs are ASCII and database comparison is ordinal, so CLR regex is
            // an independent oracle without culture or Unicode wildcard ambiguity.
            var values = new[] { "", "a", "ab", "ba", "abc", "bbb", "a_b", "%" };
            var patterns = new[] { "%", "_", "a%", "%a", "%_", "%_b", "%%", "a%%b", "%%%x", "%_%" };
            var reproduced = false;
            foreach (var pattern in patterns)
            {
                var expected = values.Select(value => Regex.IsMatch(value,
                    "\\A" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "\\z",
                    RegexOptions.CultureInvariant | RegexOptions.Singleline)).ToArray();
                var execution = Task.Run(() => Check(pattern, values, expected));
                if (!execution.Wait(TimeSpan.FromSeconds(2)))
                {
                    Console.WriteLine("BUG_2797_CONFIRMED: bounded tiny-input LIKE did not terminate; pattern=" + pattern);
                    reproduced = true;
                    continue;
                }
                if (execution.Result != null)
                {
                    Console.WriteLine("BUG_2797_CONFIRMED: " + execution.Result);
                    reproduced = true;
                }
            }
            if (reproduced) return 0;
            Console.WriteLine("VERIFIED_2797: every scalar and collection result matched the independent oracle");
            return 10;
        }
        catch (Exception failure)
        {
            // Unrelated exceptions are neither confirmed reproduction nor verified repair.
            Console.Error.WriteLine(failure);
            return 20;
        }
    }

    private static string? Check(string pattern, string[] values, bool[] expected)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
        var col = db.GetCollection("items");
        col.Insert(values.Select((value, index) => new BsonDocument { ["_id"] = index + 1, ["value"] = value }));
        for (var i = 0; i < values.Length; i++)
        {
            var actual = BsonExpression.Create("@0 LIKE @1", new BsonValue(values[i]), new BsonValue(pattern)).ExecuteScalar();
            if (!actual.IsBoolean || actual.AsBoolean != expected[i])
                return $"pattern={pattern}, input={values[i]}, expected={expected[i]}, actual={actual}";
        }
        var expectedIds = Enumerable.Range(1, values.Length).Where(i => expected[i - 1]).ToArray();
        foreach (var indexed in new[] { false, true })
        {
            if (indexed) col.EnsureIndex("value");
            var actualIds = col.Find(BsonExpression.Create("value LIKE @0", new BsonValue(pattern)))
                .Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
            if (!actualIds.SequenceEqual(expectedIds)) return "collection result mismatch, pattern=" + pattern + ", indexed=" + indexed;
        }
        if (col.Count() != values.Length) throw new InvalidOperationException("Query changed the fixture");
        return null;
    }
}
