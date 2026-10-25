namespace LiteDB.Fuzz.Targets;

internal sealed class QueryFuzzer : IFuzzTarget
{
    private static readonly BsonValue[] Values =
    {
        BsonValue.Null, BsonValue.MinValue, BsonValue.MaxValue, int.MinValue, -1, 0, int.MaxValue,
        long.MinValue, long.MaxValue, -0d, +0d, double.NaN, double.NegativeInfinity, double.PositiveInfinity,
        decimal.MinValue, decimal.MaxValue, "", "a", "A", "I", "ı", "é", "e\u0301", false, true
    };
    private static readonly BsonValue[] StoredValues = Values.Where(value => !value.IsMinValue && !value.IsMaxValue).ToArray();
    private static readonly string[] Cultures = { "en-US/None", "en-US/IgnoreCase", "tr-TR/IgnoreCase", "en-US/IgnoreNonSpace" };

    public string Name => "query";
    public string Description => "Independent boolean oracle vs unindexed, indexed, and cached SQL execution.";

    public Task RunAsync(FuzzContext context)
    {
        var collation = new Collation(Cultures[Math.Abs(context.Seed % Cultures.Length)]);
        using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
        var scan = db.GetCollection("scan");
        var indexed = db.GetCollection("indexed");
        var documents = Enumerable.Range(1, 160).Select(id => Document(context.Random, id)).ToArray();
        scan.InsertBulk(documents.Select(Clone));
        indexed.InsertBulk(documents.Select(Clone));
        indexed.EnsureIndex("score", "Score");
        indexed.EnsureIndex("nested", "Nested.Score");
        indexed.EnsureIndex("text", "Text");
        indexed.EnsureIndex("tags", "Tags[*]");
        indexed.EnsureIndex("lower_text", "LOWER(Text)");

        var checks = 0;
        var operationHits = new Dictionary<string, int>(StringComparer.Ordinal);
        while (context.Next())
        {
            if (context.Steps % 17 == 0)
            {
                var id = context.Random.Next(1, documents.Length + 1);
                var replacement = Document(context.Random, id);
                documents[id - 1] = replacement;
                context.Check(scan.Update(Clone(replacement)) && indexed.Update(Clone(replacement)),
                    "Equivalent query collections disagreed while mutating their dataset.");
            }
            var leaves = new List<Node>();
            var node = Generate(context.Random, context.Random.Next(1, 5), leaves);
            foreach (var operation in node.Operations())
                operationHits[operation] = operationHits.GetValueOrDefault(operation) + 1;
            context.ObserveNovelty("query-shape", node.Operations().OrderBy(value => value).ToArray(),
                node.Source.Length / 32);
            var parameters = Bind(context.Random, leaves);
            var expected = documents.Where(doc => node.Evaluate(doc, parameters, collation))
                .Select(doc => doc["_id"].AsInt32).OrderBy(id => id).ToArray();
            var expression = BsonExpression.Create(node.Source, parameters);
            var fromScan = scan.Query().Where(expression).OrderBy("_id").ToArray().Select(Id).ToArray();
            var fromIndex = indexed.Query().Where(expression).OrderBy("_id").ToArray().Select(Id).ToArray();
            context.Trace("query", new { source = node.Source, parameters = parameters.ToString(), expected = expected.Length });
            Equal(context, expected, fromScan, "unindexed", node.Source);
            Equal(context, expected, fromIndex, "indexed", node.Source);
            var sql = "SELECT $ FROM indexed WHERE " + node.Source + " ORDER BY _id";
            Equal(context, expected, ExecuteIds(db, sql, parameters), "SQL cold", node.Source);
            Equal(context, expected, ExecuteIds(db, sql, parameters), "SQL template publish", node.Source);
            Equal(context, expected, ExecuteIds(db, sql, parameters), "SQL cached", node.Source);
            var withTrue = BsonExpression.Create($"({node.Source}) AND true", parameters);
            Equal(context, expected, indexed.Query().Where(withTrue).OrderBy("_id").ToArray().Select(Id).ToArray(),
                "p AND true", node.Source);
            var complement = indexed.Query().Where(BsonExpression.Create($"({node.Source}) = false", parameters))
                .OrderBy("_id").ToArray().Select(Id).ToArray();
            context.Check(expected.Concat(complement).OrderBy(id => id).SequenceEqual(documents.Select(Id).OrderBy(id => id)),
                "Predicate/complement partition did not cover the dataset exactly.");
            context.Check(!expected.Intersect(complement).Any(), "Predicate and complement overlapped.");
            var offset = context.Random.Next(0, Math.Min(8, expected.Length + 1));
            var limit = context.Random.Next(1, 12);
            var pageExpected = expected.OrderByDescending(id => id).Skip(offset).Take(limit).ToArray();
            var pageActual = indexed.Query().Where(expression).OrderByDescending("_id")
                .Offset(offset).Limit(limit).ToArray().Select(Id).ToArray();
            Equal(context, pageExpected, pageActual, "descending LIMIT/OFFSET", node.Source);
            var lower = new[] { "", "a", "i", "ı", "é", "e\u0301" }[context.Random.Next(6)];
            var lowerExpected = documents.Where(document => document["Text"].IsString &&
                    collation.Compare(document["Text"].AsString.ToLowerInvariant(), lower) == 0)
                .Select(Id).OrderBy(id => id).ToArray();
            var lowerActual = indexed.Query().Where("LOWER(Text) = @0", lower).OrderBy("_id")
                .ToArray().Select(Id).ToArray();
            Equal(context, lowerExpected, lowerActual, "expression index", "LOWER(Text)");
            context.Check(indexed.Count(expression) == expected.Length, "Count disagreed with independent oracle.");
            checks += 13;
        }
        if (context.Steps >= 100)
            foreach (var required in new[] { "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE" })
                context.Check(operationHits.GetValueOrDefault(required) > 0, $"Coverage path {required} was not exercised.");
        context.Metrics["assertions"] = checks;
        context.Metrics["operationHits"] = operationHits;
        context.Metrics["collation"] = collation.ToString();
        return Task.CompletedTask;
    }

    private static BsonDocument Document(Random random, int id)
    {
        var doc = new BsonDocument { ["_id"] = id };
        if (id % 7 != 0) doc["Score"] = StoredValues[random.Next(StoredValues.Length)];
        if (id % 5 != 0) doc["Text"] = new[] { "", "a", "A", "I", "ı", "é", "e\u0301", "abc", "a_c" }[random.Next(9)];
        if (id % 11 != 0) doc["Nested"] = new BsonDocument { ["Score"] = StoredValues[random.Next(StoredValues.Length)] };
        doc["Tags"] = new BsonArray(StoredValues[random.Next(StoredValues.Length)], StoredValues[random.Next(StoredValues.Length)]);
        return doc;
    }

    private static BsonDocument Clone(BsonDocument document) => BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
    private static int Id(BsonDocument document) => document["_id"].AsInt32;

    private static int[] ExecuteIds(LiteDatabase db, string sql, BsonDocument parameters)
    {
        using var reader = db.Execute(sql, parameters);
        var result = new List<int>();
        while (reader.Read()) result.Add(reader.Current.AsDocument["_id"].AsInt32);
        return result.ToArray();
    }

    private static void Equal(FuzzContext context, int[] expected, int[] actual, string mode, string source)
    {
        context.Check(expected.SequenceEqual(actual),
            $"{mode} mismatch for {source}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");
    }

    private static BsonDocument Bind(Random random, IEnumerable<Node> leaves)
    {
        var parameters = new BsonDocument();
        foreach (var leaf in leaves)
        {
            parameters["p" + leaf.Id] = Values[random.Next(Values.Length)];
            parameters["q" + leaf.Id] = Values[random.Next(Values.Length)];
            parameters["s" + leaf.Id] = new BsonArray(Enumerable.Range(0, random.Next(1, 7)).Select(_ => Values[random.Next(Values.Length)]));
            parameters["l" + leaf.Id] = new[] { "%", "a%", "%a", "_", "a_c", "%é%" }[random.Next(6)];
        }
        return parameters;
    }

    private static Node Generate(Random random, int depth, List<Node> leaves)
    {
        if (depth > 0 && random.Next(4) != 0)
        {
            if (random.Next(5) == 0) return new Node { Operation = "NOT", Left = Generate(random, depth - 1, leaves) };
            return new Node
            {
                Operation = random.Next(2) == 0 ? "AND" : "OR",
                Left = Generate(random, depth - 1, leaves), Right = Generate(random, depth - 1, leaves)
            };
        }
        var field = new[] { "Score", "Nested.Score", "Text", "Tags[*]" }[random.Next(4)];
        if (field == "Tags[*]")
        {
            var any = new Node { Id = leaves.Count, Field = field, Operation = "ANY=" };
            leaves.Add(any);
            return any;
        }
        var operations = field == "Text" ? new[] { "=", "!=", "<", ">=", "IN", "BETWEEN", "LIKE" } :
            new[] { "=", "!=", "<", "<=", ">", ">=", "IN", "BETWEEN" };
        var node = new Node { Id = leaves.Count, Field = field, Operation = operations[random.Next(operations.Length)] };
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

        internal string Source => Left != null ? Operation == "NOT" ? $"({Left.Source}) = false" : $"({Left.Source} {Operation} {Right.Source})" :
            Operation == "ANY=" ? $"{Field} ANY = @p{Id}" :
            Operation == "IN" ? $"{Field} IN @s{Id}" : Operation == "BETWEEN" ? $"{Field} BETWEEN @p{Id} AND @q{Id}" :
            Operation == "LIKE" ? $"{Field} LIKE @l{Id}" : $"{Field} {Operation} @p{Id}";

        internal bool Evaluate(BsonDocument document, BsonDocument parameters, Collation collation)
        {
            if (Operation == "AND") return Left.Evaluate(document, parameters, collation) && Right.Evaluate(document, parameters, collation);
            if (Operation == "OR") return Left.Evaluate(document, parameters, collation) || Right.Evaluate(document, parameters, collation);
            if (Operation == "NOT") return !Left.Evaluate(document, parameters, collation);
            var value = Field switch
            {
                "Score" => document["Score"], "Text" => document["Text"],
                "Tags[*]" => document["Tags"],
                _ => document["Nested"].IsDocument ? document["Nested"]["Score"] : BsonValue.Null
            };
            if (Operation == "ANY=")
                return value.IsArray && value.AsArray.Any(item => item.CompareTo(parameters["p" + Id], collation) == 0);
            var comparison = value.CompareTo(parameters["p" + Id], collation);
            return Operation switch
            {
                "=" => comparison == 0, "!=" => comparison != 0, "<" => comparison < 0, "<=" => comparison <= 0,
                ">" => comparison > 0, ">=" => comparison >= 0,
                "IN" => parameters["s" + Id].AsArray.Any(item => value.CompareTo(item, collation) == 0),
                "BETWEEN" => comparison >= 0 && value.CompareTo(parameters["q" + Id], collation) <= 0,
                "LIKE" => value.IsString && Like(value.AsString, parameters["l" + Id].AsString, collation),
                _ => throw new InvalidOperationException(Operation)
            };
        }

        internal IEnumerable<string> Operations()
        {
            yield return Operation;
            if (Left != null)
                foreach (var operation in Left.Operations()) yield return operation;
            if (Right != null)
                foreach (var operation in Right.Operations()) yield return operation;
        }

        private static bool Like(string value, string pattern, Collation collation)
        {
            return Match(0, 0);

            bool Match(int valueIndex, int patternIndex)
            {
                while (patternIndex < pattern.Length)
                {
                    var token = pattern[patternIndex++];
                    if (token == '%')
                    {
                        if (patternIndex == pattern.Length) return true;
                        for (var retry = valueIndex; retry <= value.Length; retry++)
                            if (Match(retry, patternIndex)) return true;
                        return false;
                    }
                    if (valueIndex == value.Length) return false;
                    if (token != '_' && !collation.EqualsCharacter(value, valueIndex, pattern, patternIndex - 1)) return false;
                    valueIndex++;
                }
                return valueIndex == value.Length;
            }
        }
    }
}
