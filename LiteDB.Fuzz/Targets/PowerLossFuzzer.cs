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
            var matrixIndex = context.Steps - 1;
            var phase = Phases[matrixIndex % Phases.Length];
            var matrixOccurrence = 1 + matrixIndex / Phases.Length % 3;
            var occurrence = matrixIndex < Phases.Length * 3
                ? matrixOccurrence
                : 1 + context.Random.Next(5);
            var scenario = PowerLossScenario.Generate(context.Random, occurrence + 5);
            Exercise(context, phase, occurrence, scenario);
            context.ObserveNovelty("power-cut", phase, matrixOccurrence, occurrence);
            cuts++;
        }
        if (context.Steps >= Phases.Length)
            context.Check(cuts >= Phases.Length, "Power-loss campaign missed a crash phase.");
        context.Metrics["powerCuts"] = cuts;
        context.Metrics["durablePhases"] = Phases.Length;
        return Task.CompletedTask;
    }

    private static void Exercise(FuzzContext context, string phase, int occurrence, PowerLossScenario scenario)
    {
        using var baselineData = new DurableMemoryStream();
        using var baselineLog = new DurableMemoryStream();
        using (var seed = Open(baselineData, baselineLog, scenario.Password))
        {
            var rows = seed.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            rows.Insert(Enumerable.Range(1, 4).Select(id => Document(id, id * 10, id * 2500)));
            using var source = new MemoryStream(scenario.InitialFile);
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
        var acknowledgedFile = scenario.InitialFile;
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
            db = Open(data, log, scenario.Password);
            var rows = db.GetCollection("rows");
            for (var attempt = 0; attempt < scenario.Transactions.Length && !fired; attempt++)
            {
                inFlightState = Clone(acknowledgedState);
                inFlightFile = acknowledgedFile.ToArray();
                context.Check(db.BeginTrans(), "Power-loss scenario could not begin a transaction.");
                foreach (var operation in scenario.Transactions[attempt].Operations)
                {
                    Apply(rows, db, inFlightState, ref inFlightFile, operation);
                }
                context.Trace("power-transaction", new
                {
                    phase, occurrence, attempt,
                    operations = scenario.Transactions[attempt].Operations.Select(operation => new
                    {
                        operation.Kind, operation.Id, operation.Value, operation.PayloadLength,
                        operation.FileLength, operation.Pattern
                    })
                });
                context.Check(db.Commit(), "Power-loss scenario commit returned false.");
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
        using (var recovered = Open(recoveredData, recoveredLog, scenario.Password))
        {
            var rows = recovered.GetCollection("rows");
            var actual = rows.Query().OrderBy("_id").ToArray();
            var acknowledgedDocuments = acknowledgedState.Values.OrderBy(document => document["_id"]).ToArray();
            var possibleDocuments = (inFlightState ?? acknowledgedState).Values
                .OrderBy(document => document["_id"]).ToArray();
            isCommitted = FuzzOracle.DocumentsEqual(actual, acknowledgedDocuments);
            var includesInFlight = FuzzOracle.DocumentsEqual(actual, possibleDocuments);
            using var output = new MemoryStream();
            recovered.FileStorage.Download("power-file", output);
            var recoveredFile = output.ToArray();
            var acknowledgedFileMatches = recoveredFile.SequenceEqual(acknowledgedFile);
            var inFlightFileMatches = inFlightFile != null && recoveredFile.SequenceEqual(inFlightFile);
            FuzzOracle.VerifyAtomicState(context, isCommitted, acknowledgedFileMatches,
                includesInFlight, inFlightFileMatches,
                "Power loss mixed document and FileStorage states from different transactions.");
            context.Check(!acknowledged || isCommitted || includesInFlight,
                "Power loss discarded a commit that had already been acknowledged.");
            var values = rows.Query().OrderBy("Value").ToArray().Select(row => row["Value"].AsInt32).ToArray();
            FuzzOracle.VerifySequence(context, values,
                actual.Select(row => row["Value"].AsInt32).OrderBy(value => value),
                "Power-loss recovery produced invalid secondary-index order.");
            recovered.Checkpoint();
        }
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, $"power-{phase}.db"));
        File.WriteAllBytes(file, recoveredData.DurableBytes);
        DatabaseIntegrityVerifier.Verify(context, file, scenario.Password);
        context.Trace("power-cut-result", new
        {
            phase, occurrence, phaseHits, acknowledged,
            encrypted = scenario.Password != null,
            transactions = scenario.Transactions.Length,
            recovered = isCommitted ? "acknowledged-prefix" : "in-flight-durable"
        });
    }

    private static void Apply(ILiteCollection<BsonDocument> rows, LiteDatabase db,
        Dictionary<int, BsonDocument> state, ref byte[] file, PowerLossOperation operation)
    {
        switch (operation.Kind)
        {
            case 0:
            case 1:
            case 2:
                var document = Document(operation.Id, operation.Value, operation.PayloadLength);
                state[operation.Id] = document;
                rows.Upsert(Clone(document));
                break;
            case 3:
                state.Remove(operation.Id);
                rows.Delete(operation.Id);
                break;
            default:
                file = PowerLossScenario.Bytes(operation.FileLength, operation.Pattern);
                using (var source = new MemoryStream(file))
                    db.FileStorage.Upload("power-file", "replacement.bin", source);
                break;
        }
    }

    private static BsonDocument Document(int id, int value, int payload) => new()
    {
        ["_id"] = id, ["Value"] = value, ["Payload"] = new byte[payload]
    };

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
