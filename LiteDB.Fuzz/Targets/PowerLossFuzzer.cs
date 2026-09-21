using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class PowerLossFuzzer : IFuzzTarget
{
    private static readonly string[] Phases =
    {
        "wal-page-before-write", "wal-page-after-write",
        "wal-confirmation-before-write", "wal-confirmation-after-write",
        "wal-before-durable-flush", "wal-after-durable-flush",
        "wal-before-index-confirmation", "wal-after-index-confirmation",
        "checkpoint-before-page-write", "checkpoint-after-page-write",
        "checkpoint-before-data-flush", "checkpoint-after-data-flush",
        "checkpoint-before-clear", "checkpoint-after-clear"
    };

    public string Name => "power-loss";
    public string Description => "Deterministic volatile/durable device model with power cuts at WAL and checkpoint gates.";

    public Task RunAsync(FuzzContext context)
    {
        var cuts = 0;
        while (context.Next())
        {
            var phase = Phases[(context.Steps - 1) % Phases.Length];
            var occurrence = 1 + (context.Steps / Phases.Length) % 3;
            Exercise(context, phase, occurrence);
            context.ObserveNovelty("power-cut", phase, occurrence);
            cuts++;
        }
        if (context.Steps >= Phases.Length)
            context.Check(cuts >= Phases.Length, "Power-loss campaign missed a crash phase.");
        context.Metrics["powerCuts"] = cuts;
        context.Metrics["durablePhases"] = Phases.Length;
        return Task.CompletedTask;
    }

    private static void Exercise(FuzzContext context, string phase, int occurrence)
    {
        using var baselineData = new DurableMemoryStream();
        using var baselineLog = new DurableMemoryStream();
        var password = context.Steps % 2 == 0 ? "power-password" : null;
        var initialFile = Enumerable.Range(0, 9000).Select(index => (byte)index).ToArray();
        using (var seed = Open(baselineData, baselineLog, password))
        {
            var rows = seed.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            rows.Insert(Enumerable.Range(1, 4).Select(id => Document(id, id * 10, id * 2500)));
            using var source = new MemoryStream(initialFile);
            seed.FileStorage.Upload("power-file", "baseline.bin", source);
            seed.Checkpoint();
        }

        using var data = baselineData.CloneDurable();
        using var log = baselineLog.CloneDurable();
        var fired = false;
        var acknowledged = false;
        var phaseHits = 0;
        var acknowledgedState = Enumerable.Range(1, 4)
            .ToDictionary(id => id, id => Document(id, id * 10, id * 2500));
        var acknowledgedFile = initialFile;
        Dictionary<int, BsonDocument> inFlightState = null;
        byte[] inFlightFile = null;
        LiteDatabase db = null;
        EngineState.SimulateProcessCrash = current =>
        {
            if (current != phase) return;
            if (++phaseHits != occurrence) return;
            fired = true;
            data.PowerCut();
            log.PowerCut();
            throw new PowerCutException(phase);
        };
        try
        {
            db = Open(data, log, password);
            var rows = db.GetCollection("rows");
            for (var attempt = 1; attempt <= occurrence; attempt++)
            {
                var operation = (context.Steps + attempt) % 5;
                inFlightState = Clone(acknowledgedState);
                inFlightFile = acknowledgedFile.ToArray();
                switch (operation)
                {
                    case 0:
                        var added = Document(10 + attempt, context.Steps * 10 + attempt,
                            9000 + attempt * 3000);
                        inFlightState[added["_id"].AsInt32] = added;
                        rows.Upsert(Clone(added));
                        break;
                    case 1:
                        var updated = Document(1, -context.Steps - attempt, 16000 + attempt * 2000);
                        inFlightState[1] = updated;
                        rows.Update(Clone(updated));
                        break;
                    case 2:
                        inFlightState.Remove(4);
                        rows.Delete(4);
                        break;
                    case 3:
                        context.Check(rows.DropIndex("value"),
                            "Generated power-loss index mutation unexpectedly became a no-op.");
                        break;
                    case 4:
                        inFlightFile = Enumerable.Range(0, 12000 + attempt * 1000)
                            .Select(index => (byte)(index ^ context.Steps)).ToArray();
                        using (var source = new MemoryStream(inFlightFile))
                            db.FileStorage.Upload("power-file", "replacement.bin", source);
                        break;
                }
                acknowledgedState = inFlightState;
                acknowledgedFile = inFlightFile;
                acknowledged = true;
                inFlightState = null;
                inFlightFile = null;
                if (phase.StartsWith("checkpoint-", StringComparison.Ordinal)) db.Checkpoint();
            }
        }
        catch (Exception) when (fired) { }
        finally
        {
            EngineState.SimulateProcessCrash = null;
            try { db?.Dispose(); }
            catch when (fired) { }
        }
        context.Check(fired, $"Power-loss phase {phase} was not reached.");

        using var recoveredData = data.CloneDurable();
        using var recoveredLog = log.CloneDurable();
        var isCommitted = false;
        using (var recovered = Open(recoveredData, recoveredLog, password))
        {
            var rows = recovered.GetCollection("rows");
            var actual = rows.Query().OrderBy("_id").ToArray();
            var acknowledgedDocuments = acknowledgedState.Values.OrderBy(document => document["_id"]).ToArray();
            var possibleDocuments = (inFlightState ?? acknowledgedState).Values
                .OrderBy(document => document["_id"]).ToArray();
            isCommitted = Equal(actual, acknowledgedDocuments);
            var includesInFlight = Equal(actual, possibleDocuments);
            context.Check(isCommitted || includesInFlight,
                "Power loss exposed a partially durable transaction.");
            context.Check(!acknowledged || isCommitted || includesInFlight,
                "Power loss discarded a commit that had already been acknowledged.");
            using var output = new MemoryStream();
            recovered.FileStorage.Download("power-file", output);
            context.Check(output.ToArray().SequenceEqual(acknowledgedFile) ||
                inFlightFile != null && output.ToArray().SequenceEqual(inFlightFile),
                "Power loss exposed a partial FileStorage replacement.");
            var values = rows.Query().OrderBy("Value").ToArray().Select(row => row["Value"].AsInt32).ToArray();
            context.Check(values.SequenceEqual(actual.Select(row => row["Value"].AsInt32).OrderBy(value => value)),
                "Power-loss recovery produced invalid secondary-index order.");
            recovered.Checkpoint();
        }
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, $"power-{phase}.db"));
        File.WriteAllBytes(file, recoveredData.DurableBytes);
        DatabaseIntegrityVerifier.Verify(context, file, password);
        context.Trace("power-cut-result", new { phase, occurrence, phaseHits, acknowledged, recovered = isCommitted ? "acknowledged-prefix" : "in-flight-durable" });
    }

    private static BsonDocument Document(int id, int value, int payload) => new()
    {
        ["_id"] = id, ["Value"] = value, ["Payload"] = new byte[payload]
    };

    private static bool Equal(BsonDocument[] actual, BsonDocument[] expected) => actual.Length == expected.Length &&
        actual.Zip(expected, (left, right) => BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right))).All(value => value);

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));

    private static Dictionary<int, BsonDocument> Clone(Dictionary<int, BsonDocument> documents) =>
        documents.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));

    private static LiteDatabase Open(Stream data, Stream log, string password) => new(new LiteEngine(new EngineSettings
    {
        DataStream = data,
        LogStream = log,
        Password = password,
        DurableCommits = true,
        TransactionPageLimit = 16
    }));

    private sealed class PowerCutException : IOException
    {
        internal PowerCutException(string phase) : base("Power cut at " + phase) { }
    }

    private sealed class DurableMemoryStream : MemoryStream, IDurableStream
    {
        private byte[] _durable = Array.Empty<byte>();
        private bool _powered = true;

        internal byte[] DurableBytes => _durable.ToArray();

        internal DurableMemoryStream CloneDurable() => new(_durable);

        private DurableMemoryStream(byte[] bytes)
        {
            base.Write(bytes, 0, bytes.Length);
            Position = 0;
            _durable = bytes.ToArray();
        }

        internal DurableMemoryStream() { }

        public void FlushToDisk()
        {
            EnsurePower();
            _durable = ToArray();
        }

        internal void PowerCut() => _powered = false;

        public override void Flush() => EnsurePower();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsurePower();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsurePower();
            base.Write(buffer);
        }

        public override void SetLength(long value)
        {
            EnsurePower();
            base.SetLength(value);
        }

        private void EnsurePower()
        {
            if (!_powered) throw new IOException("The modeled device has lost power.");
        }
    }
}
