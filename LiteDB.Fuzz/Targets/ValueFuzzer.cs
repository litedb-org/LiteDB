namespace LiteDB.Fuzz.Targets;

internal sealed class ValueFuzzer : IFuzzTarget
{
    private static readonly BsonValue[] Values =
    {
        BsonValue.MinValue, BsonValue.Null, false, true, int.MinValue, -1, 0, 1, int.MaxValue,
        long.MinValue, long.MaxValue, -0d, +0d, double.NaN, double.NegativeInfinity, double.PositiveInfinity,
        decimal.MinValue, 0m, decimal.MaxValue, "", "a", "A", "é", "e\u0301",
        new byte[0], new byte[] { 0, 1, 255 }, new ObjectId("0123456789abcdef01234567"), Guid.Empty,
        DateTime.UnixEpoch, BsonValue.MaxValue
    };

    public string Name => "value";
    public string Description => "Cheap comparison, JSON, ObjectId, connection-string, tokenizer, and auto-id laws.";

    public Task RunAsync(FuzzContext context)
    {
        var collation = context.Seed % 2 == 0 ? Collation.Binary : new Collation("tr-TR/IgnoreCase");
        using var db = new LiteDatabase(":memory:");
        var auto = db.GetCollection("auto");
        var ids = new HashSet<BsonValue>();
        var generatedIds = 0L;
        while (context.Next())
        {
            if (ids.Count >= 1_000)
            {
                db.DropCollection("auto");
                auto = db.GetCollection("auto");
                ids.Clear();
            }
            var a = Values[context.Random.Next(Values.Length)];
            var b = Values[context.Random.Next(Values.Length)];
            var c = Values[context.Random.Next(Values.Length)];
            var ab = a.CompareTo(b, collation);
            var ba = b.CompareTo(a, collation);
            context.Check(a.CompareTo(a, collation) == 0, "Compare(a,a) was not zero.");
            context.Check(Math.Sign(ab) == -Math.Sign(ba), "BsonValue comparison sign symmetry failed.");
            if (ab <= 0 && b.CompareTo(c, collation) <= 0)
                context.Check(a.CompareTo(c, collation) <= 0, "BsonValue comparison transitivity failed.");

            var document = new BsonDocument { ["value"] = a };
            var json = JsonSerializer.Serialize(document);
            var fromText = JsonSerializer.Deserialize(json).AsDocument;
            var fromReader = JsonSerializer.Deserialize(new StringReader(json)).AsDocument;
            context.Check(fromText["value"].Equals(fromReader["value"]), "JSON string and streaming readers disagreed.");

            var objectId = new ObjectId(RandomBytes(context.Random, 12));
            context.Check(new ObjectId(objectId.ToByteArray()).Equals(objectId), "ObjectId byte round trip failed.");
            context.Check(new ObjectId(objectId.ToString()).Equals(objectId), "ObjectId text round trip failed.");

            var connection = new ConnectionString
            {
                Filename = "db;" + context.Steps + "=value.db", Password = context.Steps % 3 == 0 ? "" : "p;=\"" + context.Steps,
                DurableCommits = context.Steps % 2 == 0, ReadOnly = context.Steps % 5 == 0,
                Collation = collation, TransactionPageLimit = 3 + context.Steps % 20
            };
            var parsed = new ConnectionString(connection.ToStringWithPassword());
            context.Check(parsed.Filename == connection.Filename && parsed.Password == connection.Password &&
                parsed.DurableCommits == connection.DurableCommits && parsed.ReadOnly == connection.ReadOnly &&
                parsed.TransactionPageLimit == connection.TransactionPageLimit,
                "ConnectionString parse/format/parse changed an option.");

            Tokenize(context, RandomText(context.Random, 80));
            var id = auto.Insert(new BsonDocument { ["value"] = context.Steps });
            context.Check(ids.Add(id), "Auto-id generation produced a duplicate.");
            generatedIds++;
            context.Trace("value-laws", new { a = a.ToString(), b = b.ToString(), jsonLength = json.Length });
        }
        context.Metrics["autoIds"] = generatedIds;
        context.Metrics["collation"] = collation.ToString();
        return Task.CompletedTask;
    }

    private static void Tokenize(FuzzContext context, string input)
    {
        var tokenizer = new Tokenizer(input);
        var tokens = 0;
        try
        {
            while (true)
            {
                var token = tokenizer.ReadToken(eatWhitespace: false);
                tokens++;
                if (token.Type == TokenType.EOF) break;
                context.Check(tokens <= input.Length * 2 + 2, "Tokenizer failed to make input progress.");
            }
        }
        catch (Exception error) when (error is LiteException or FormatException)
        {
        }
    }

    private static string RandomText(Random random, int maximum)
    {
        const string alphabet = "abcXYZ012_@'$\"[]{}(),.;:+-*/%<>=! \\n\t\0éı";
        return new string(Enumerable.Range(0, random.Next(maximum + 1)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
