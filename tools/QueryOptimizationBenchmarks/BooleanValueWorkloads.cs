using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class BooleanValueWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter != null && !filter.StartsWith("boolvalue", StringComparison.Ordinal) &&
            !"boolvalue".StartsWith(filter, StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        var expected = Enumerable.Range(1, 20000).Where(i => i > 10000 || i % 1000 == 7).Sum(i => (long)i);
        measure("boolvalue-compound-linq", 5, i => Check(rows.Query()
            .Where(x => x.Name != "missing" && (x.Score > 10000 || x.City == "City7"))
            .Select(x => x.Id).ToList().Sum(x => (long)x), expected));
        measure("boolvalue-compound-sql", 5, i => Check(ReadIds(
            "SELECT _id FROM rows WHERE Name != 'missing' AND (Score > 10000 OR City = 'City7')"), expected));

        var projected = Enumerable.Range(1, 20000).Sum(i => Flags(i,
            i > 10000 && ("Person" + i).Contains("1"), i <= 10000 || i % 1000 == 7, i >= 20000));
        measure("boolvalue-projected-linq", 5, i => Check(rows.Query().Select(x => new
        {
            x.Id, First = x.Score > 10000 && x.Name.Contains("1"),
            Second = x.Score <= 10000 || x.City == "City7", Third = x.Score >= 20000
        }).ToList().Sum(x => Flags(x.Id, x.First, x.Second, x.Third)), projected));
        measure("boolvalue-projected-sql", 5, i =>
        {
            using var reader = db.Execute("SELECT { _id: _id, first: Score > 10000 AND Name LIKE '%1%', " +
                "second: Score <= 10000 OR City = 'City7', third: Score >= 20000 } FROM rows");
            long sum = 0;
            while (reader.Read()) sum += Flags(reader.Current["_id"].AsInt32, reader.Current["first"].AsBoolean,
                reader.Current["second"].AsBoolean, reader.Current["third"].AsBoolean);
            return Check(sum, projected);
        });
        var residual = Enumerable.Range(1, 20000).Where(i => i % 1000 == 234 &&
            (i > 10000 && ("Person" + i).Contains("1") || i < 10)).Sum(i => (long)i);
        measure("boolvalue-residual-linq", 1000, i => Check(rows.Query().Where(x => x.City == "City234" &&
            (x.Score > 10000 && x.Name.Contains("1") || x.Score < 10)).ToList().Sum(x => (long)x.Id), residual));
        measure("boolvalue-id-control", 4000, i => rows.FindById(i % 20000 + 1).Id);
        measure("boolvalue-covered-count-control", 1000, i => Check(rows.Count(x => x.Score >= 10000 && x.Score < 10100), 100));
        plans["boolvalue-compound"] = rows.Query().Where(x => x.Name != "missing" && (x.Score > 10000 || x.City == "City7"))
            .Select(x => x.Id).GetPlan().ToString();
        plans["boolvalue-residual"] = rows.Query().Where(x => x.City == "City234" &&
            (x.Score > 10000 && x.Name.Contains("1") || x.Score < 10)).GetPlan().ToString();

        var arrays = db.GetCollection<ArrayRow>("boolarrays");
        arrays.InsertBulk(Enumerable.Range(1, 4000).Select(i => new ArrayRow
        {
            Id = i, Values = Enumerable.Range(0, 32).ToArray(),
            Labels = Enumerable.Range(0, 32).Select(v => "label-" + v).ToArray()
        }));
        const long nestedExpected = 8002000 + 4000L * 8 * 10000;
        measure("boolvalue-nested-map-linq", 3, i =>
        {
            var minimum = i % 3 + 4;
            var maximum = minimum + 8;
            return Check(arrays.Query().Select(x => new
            {
                x.Id, Flags = x.Values.Select(v => v >= minimum && v < maximum).ToArray()
            }).ToList().Sum(x => x.Id + x.Flags.Count(v => v) * 10000L), nestedExpected);
        });
        measure("boolvalue-nested-map-sql", 3, i =>
        {
            var minimum = i % 3 + 4;
            using var reader = db.Execute("SELECT { _id: _id, flags: ARRAY(MAP(Values => @ >= @minimum AND @ < @maximum)) } " +
                "FROM boolarrays", new BsonDocument { ["minimum"] = minimum, ["maximum"] = minimum + 8 });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32 + reader.Current["flags"].AsArray.Count(x => x.AsBoolean) * 10000L;
            return Check(sum, nestedExpected);
        });
        measure("boolvalue-nested-any-like", 3, i => Check(ReadIds(
            "SELECT _id FROM boolarrays WHERE Labels ANY LIKE 'label-31'"), 8002000));
        measure("boolvalue-nested-all-between", 3, i => Check(ReadIds(
            "SELECT _id FROM boolarrays WHERE Values ALL BETWEEN 0 AND 31"), 8002000));
        measure("boolvalue-nested-array-control", 3, i => Check(arrays.Query().Select(x => new { x.Id, x.Values })
            .ToList().Sum(x => x.Id + x.Values.Sum() * 10000L), 8002000 + 4000L * 496 * 10000));

        long ReadIds(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }

    private static long Flags(int id, bool first, bool second, bool third) =>
        id + (first ? 100000L : 0) + (second ? 1000000L : 0) + (third ? 10000000L : 0);

    private static long Check(long result, long expected)
    {
        if (result != expected) throw new InvalidOperationException("Boolean query returned unexpected fixture results.");
        return result;
    }

    public class ArrayRow
    {
        public int Id { get; set; }
        public int[] Values { get; set; }
        public string[] Labels { get; set; }
    }
}
