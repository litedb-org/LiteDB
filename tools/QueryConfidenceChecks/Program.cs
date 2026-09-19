using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class Program
{
    private static readonly BsonValue[] Values =
    {
        BsonValue.Null, -5, -1, 0, 0L, 0.0, 0m, 1, 5,
        "", "a", "A", "I", "i", "ı", "İ", "é", "e", "ß", "ss", "z", false, true,
        new byte[] { 1, 2 }, new BsonArray(1, 2), new BsonArray(2, 3), new BsonDocument { ["x"] = 1 }
    };
    private static readonly BsonValue[] Bounds = Values.Concat(new[] { BsonValue.MinValue, BsonValue.MaxValue }).ToArray();
    private static int _cases;
    private static int _checks;
    private static string _scenario;

    private static void Main(string[] args)
    {
        var seed = args.Length > 0 ? int.Parse(args[0]) : 2905;
        var shapes = args.Length > 1 ? int.Parse(args[1]) : 150;
        try
        {
            foreach (var culture in new[] { "en-US/None", "en-US/IgnoreCase", "tr-TR/IgnoreCase", "en-US/IgnoreNonSpace" })
            {
                Run(culture, seed, shapes);
                Console.WriteLine($"PASS {culture}: {_cases} cases / {_checks} assertions cumulative");
            }
            Console.WriteLine($"PASS seed={seed}, shapes={shapes}: {_cases} cases / {_checks} assertions; assembly={typeof(LiteDatabase).Assembly.Location}");
        }
        catch
        {
            Console.Error.WriteLine($"FAILED seed={seed}: {_scenario}");
            throw;
        }
    }

    private static void Run(string culture, int seed, int shapes)
    {
        var collation = new Collation(culture);
        using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
        var rows = db.GetCollection("rows");
        var random = new Random(seed);
        rows.InsertBulk(Enumerable.Range(1, 256).Select(i =>
        {
            var document = new BsonDocument { ["_id"] = i };
            if (i % 7 != 0) document["Score"] = Values[random.Next(Values.Length)];
            if (i % 11 != 0) document["Owner"] = new BsonDocument { ["Score"] = Values[random.Next(Values.Length)] };
            return document;
        }));
        var documents = rows.FindAll().ToArray();
        rows.EnsureIndex("score", "score");
        rows.EnsureIndex("nested", "owner.score");

        for (var shape = 0; shape < shapes; shape++)
        {
            var leaves = new List<Node>();
            var root = Generate(random, 1 + shape % 5, shape % 3, leaves);
            var sql = "SELECT $ FROM rows WHERE " + root.Source + " ORDER BY _id";
            BsonExpression template = null;
            for (var binding = 0; binding < 4; binding++)
            {
                var parameters = new BsonDocument();
                foreach (var leaf in leaves)
                {
                    parameters["p" + leaf.Id] = Bounds[random.Next(Bounds.Length)];
                    parameters["q" + leaf.Id] = Bounds[random.Next(Bounds.Length)];
                    parameters["s" + leaf.Id] = new BsonArray(Enumerable.Range(0, random.Next(10))
                        .Select(_ => Bounds[random.Next(Bounds.Length)]));
                }
                _scenario = $"{culture}, shape={shape}, binding={binding}: {root.Source}\n{parameters}";
                template ??= BsonExpression.Create(root.Source, parameters);
                var expression = template.Bind(parameters);
                // The oracle walks its own tree over materialized documents. It
                // shares BSON comparison, but no parser, expression evaluator or planner.
                var expected = documents.Where(d => root.Evaluate(d, parameters, collation))
                    .OrderBy(d => d["_id"].AsInt32).ToArray();
                Check(expected, rows.Query().Where(expression).OrderBy("_id").ToArray(), "binding");
                using (var reader = db.Execute(sql, parameters))
                {
                    var result = new List<BsonDocument>();
                    while (reader.Read()) result.Add(reader.Current.AsDocument);
                    Check(expected, result, "SQL cache");
                }
                if (rows.Count(expression) != expected.Length) throw new Exception("Count mismatch");
                _checks++;
                var offset = random.Next(30);
                var limit = random.Next(1, 30);
                var sorted = expected.OrderBy(d => d["Score"], Comparer<BsonValue>.Create((a, b) => a.CompareTo(b, collation)))
                    .ThenByDescending(d => d["_id"].AsInt32).Skip(offset).Take(limit);
                Check(sorted, rows.Query().Where(expression).OrderBy("Score").ThenByDescending("_id")
                    .Offset(offset).Limit(limit).ToArray(), "pagination");
                _cases++;
            }
        }
    }

    private static void Check(IEnumerable<BsonDocument> expected, IEnumerable<BsonDocument> actual, string mode)
    {
        var wanted = expected.Select(d => d["_id"].AsInt32).ToArray();
        var found = actual.Select(d => d["_id"].AsInt32).ToArray();
        if (!wanted.SequenceEqual(found))
            throw new Exception($"{mode} mismatch: expected={string.Join(",", wanted)}; actual={string.Join(",", found)}");
        _checks++;
    }

    private static Node Generate(Random random, int depth, int fieldMode, List<Node> leaves)
    {
        if (depth > 0 && random.Next(4) != 0)
        {
            var left = Generate(random, depth - 1, fieldMode, leaves);
            var right = Generate(random, depth - 1, fieldMode, leaves);
            return new Node { Operation = random.Next(2) == 0 ? "AND" : "OR", Left = left, Right = right };
        }
        var node = new Node
        {
            Operation = new[] { "=", "!=", "<", "<=", ">", ">=", "IN", "BETWEEN" }[random.Next(8)],
            Id = leaves.Count,
            Field = fieldMode == 0 ? "Score" : fieldMode == 1 ? "Owner.Score" : random.Next(2) == 0 ? "Score" : "Owner.Score"
        };
        leaves.Add(node);
        return node;
    }

    private sealed class Node
    {
        internal string Operation;
        internal string Field;
        internal int Id;
        internal Node Left;
        internal Node Right;

        internal string Source => Left != null ? $"({Left.Source} {Operation} {Right.Source})" :
            Operation == "IN" ? $"{Field} IN @s{Id}" : Operation == "BETWEEN" ?
            $"{Field} BETWEEN @p{Id} AND @q{Id}" : $"{Field} {Operation} @p{Id}";

        internal bool Evaluate(BsonDocument document, BsonDocument parameters, Collation collation)
        {
            if (Operation == "AND") return Left.Evaluate(document, parameters, collation) && Right.Evaluate(document, parameters, collation);
            if (Operation == "OR") return Left.Evaluate(document, parameters, collation) || Right.Evaluate(document, parameters, collation);
            var value = Field == "Score" ? document["Score"] : document["Owner"].IsDocument ? document["Owner"]["Score"] : BsonValue.Null;
            var comparison = value.CompareTo(parameters["p" + Id], collation);
            switch (Operation)
            {
                case "=": return comparison == 0;
                case "!=": return comparison != 0;
                case "<": return comparison < 0;
                case "<=": return comparison <= 0;
                case ">": return comparison > 0;
                case ">=": return comparison >= 0;
                case "IN": return parameters["s" + Id].AsArray.Any(x => value.CompareTo(x, collation) == 0);
                case "BETWEEN": return comparison >= 0 && value.CompareTo(parameters["q" + Id], collation) <= 0;
                default: throw new InvalidOperationException(Operation);
            }
        }
    }
}
