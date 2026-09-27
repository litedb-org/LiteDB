namespace LiteDB.Fuzz.Targets;

internal sealed class SortFuzzer : IFuzzTarget
{
    public string Name => "sort";
    public string Description => "Full external sort and Top-N/offset/limit differential checks with long keys and ties.";

    public Task RunAsync(FuzzContext context)
    {
        var previousSpill = LiteDB.Engine.EngineState.ObserveSortSpill;
        var spills = 0;
        LiteDB.Engine.EngineState.ObserveSortSpill = _ => Interlocked.Increment(ref spills);
        var collation = context.Seed % 2 == 0 ? Collation.Binary : new Collation("en-US/IgnoreCase");
        using var data = new MemoryStream();
        using var temporary = new MemoryStream();
        using var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
        {
            DataStream = data, TempStream = temporary, Collation = collation
        }));
        var rows = db.GetCollection("rows");
        var documents = new List<BsonDocument>();
        var nextId = 1;
        var topNChecks = 0;
        var longKeyDocuments = 0;
        try
        {
            VerifyActualSpill(context, db, temporary, ref rows, ref documents, ref nextId, ref spills);
            VerifyTempFailureCleanup(context);
            while (context.Next())
            {
                if (documents.Count >= 2_000)
                {
                    db.DropCollection("rows");
                    rows = db.GetCollection("rows");
                    documents.Clear();
                    nextId = 1;
                }
                for (var i = 0; i < 40; i++)
                {
                    var document = new BsonDocument
                    {
                        ["_id"] = nextId++, ["Key"] = Key(context.Random, i),
                        ["Tie"] = i % 7, ["Payload"] = new string('p', context.Random.Next(30, 400))
                    };
                    rows.Insert(document);
                    documents.Add(document);
                    if (document["Key"].IsString && document["Key"].AsString.Length > 255 ||
                        document["Key"].IsBinary && document["Key"].AsBinary.Length > 255) longKeyDocuments++;
                }
                var offset = context.Random.Next(0, Math.Min(60, documents.Count));
                var limit = context.Random.Next(1, Math.Min(80, documents.Count - offset) + 1);
                var comparer = Comparer<BsonValue>.Create((left, right) => left.CompareTo(right, collation));
                var expected = documents.OrderBy(document => document["Key"], comparer)
                    .ThenBy(document => document["Tie"], comparer).ThenBy(document => document["_id"].AsInt32)
                    .Skip(offset).Take(limit).Select(Id).ToArray();
                var actual = rows.Query().OrderBy("Key").ThenBy("Tie").ThenBy("_id")
                    .Offset(offset).Limit(limit).ToArray().Select(Id).ToArray();
                context.Check(expected.SequenceEqual(actual),
                    $"Top-N sort mismatch at offset={offset}, limit={limit}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");

                var descendingExpected = documents.OrderByDescending(document => document["Key"], comparer)
                    .ThenByDescending(document => document["_id"].AsInt32).Take(limit).Select(Id).ToArray();
                var descendingActual = rows.Query().OrderByDescending("Key").ThenByDescending("_id")
                    .Limit(limit).ToArray().Select(Id).ToArray();
                context.Check(descendingExpected.SequenceEqual(descendingActual), "Descending full-sort/Top-N mismatch.");
                topNChecks += 2;
                context.Trace("sort", new { documents = documents.Count, offset, limit });
            }
        }
        finally { LiteDB.Engine.EngineState.ObserveSortSpill = previousSpill; }
        if (context.Steps >= 10)
            context.Check(topNChecks >= 20 && longKeyDocuments > 0, "Sort campaign missed Top-N or extended-length keys.");
        context.Metrics["topNChecks"] = topNChecks;
        context.Metrics["longKeyDocuments"] = longKeyDocuments;
        context.Metrics["documents"] = documents.Count;
        context.Metrics["collation"] = collation.ToString();
        context.Metrics["observedSortSpills"] = spills;
        return Task.CompletedTask;
    }

    private static void VerifyEarlyDisposal(FuzzContext context, ILiteCollection<BsonDocument> rows)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var reader = rows.Query().OrderBy("Key").ThenBy("Tie").ToEnumerable().GetEnumerator();
            context.Check(reader.MoveNext(), "Spilled sort returned no row before early disposal.");
        }
        context.Metrics["earlySpilledCursorDisposals"] = 8;
    }

    private static void VerifyTempFailureCleanup(FuzzContext context)
    {
        using var temporary = new ThrowingTempStream(128 * 1024);
        Exception failure = null;
        try
        {
            using var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
            {
                DataStream = new MemoryStream(), TempStream = temporary
            }));
            var rows = db.GetCollection("temp_failure");
            rows.InsertBulk(Enumerable.Range(1, 3500).Select(id => new BsonDocument
            {
                ["_id"] = id, ["key"] = new string((char)('a' + id % 19), 500)
            }));
            _ = rows.Query().OrderBy("key").ThenBy("_id").ToArray();
        }
        catch (Exception error) { failure = error; }
        context.Check(temporary.Failures > 0 && failure is IOException,
            $"Temporary-sort I/O failure was not exercised cleanly: {failure?.GetType().FullName}.");
        context.Check(temporary.CanRead && temporary.CanWrite,
            "Sort cleanup disposed a caller-owned temporary stream.");
    }

    private static void VerifyActualSpill(FuzzContext context, LiteDatabase db, MemoryStream temporary,
        ref ILiteCollection<BsonDocument> rows, ref List<BsonDocument> documents, ref int nextId, ref int spills)
    {
        var pressure = Enumerable.Range(1, 3500).Select(id => new BsonDocument
        {
            ["_id"] = id, ["Key"] = new string((char)('a' + id % 23), 400 + id % 200), ["Tie"] = id % 11
        }).ToArray();
        rows.InsertBulk(pressure);
        var result = rows.Query().OrderBy("Key").ThenBy("Tie").ThenBy("_id").ToArray();
        context.Check(result.Length == pressure.Length && spills > 0,
            "External-sort campaign did not prove a temporary-disk spill.");
        var lengthAfterFullSort = temporary.Length;
        VerifyEarlyDisposal(context, rows);
        context.Check(temporary.Length <= lengthAfterFullSort,
            "Repeated early cursor disposal grew temporary sort storage without bound.");
        context.Metrics["boundedSortTempBytes"] = temporary.Length;
        db.DropCollection("rows");
        rows = db.GetCollection("rows");
        documents = new List<BsonDocument>();
        nextId = 1;
    }

    private static BsonValue Key(Random random, int ordinal)
    {
        return (ordinal % 6) switch
        {
            0 => new string((char)('a' + ordinal % 5), 260 + random.Next(500)),
            1 => Bytes(random, 260 + random.Next(500)),
            2 => ordinal % 4,
            3 => "Tie" + ordinal % 3,
            4 => new string('é', 140 + random.Next(100)),
            _ => BsonValue.Null
        };
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static int Id(BsonDocument document) => document["_id"].AsInt32;

    private sealed class ThrowingTempStream : MemoryStream
    {
        private readonly long _limit;
        internal int Failures { get; private set; }
        internal ThrowingTempStream(long limit) => _limit = limit;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > _limit) { Failures++; throw new IOException("Injected sort-temp failure."); }
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > _limit) { Failures++; throw new IOException("Injected sort-temp failure."); }
            base.Write(buffer);
        }
    }
}
