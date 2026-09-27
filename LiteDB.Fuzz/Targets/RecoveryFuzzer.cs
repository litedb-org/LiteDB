namespace LiteDB.Fuzz.Targets;

internal sealed class RecoveryFuzzer : IFuzzTarget
{
    public string Name => "recovery";
    public string Description => "Generated dirty-WAL recovery interrupted by transient and persistent read/write failures.";

    public Task RunAsync(FuzzContext context)
    {
        var interruptions = 0;
        while (context.Next())
        {
            var baseline = CreateDirtyState(context);
            using var data = new FaultStream(baseline.Data);
            using var log = new FaultStream(baseline.Log);
            var selected = context.Steps % 2 == 0 ? data : log;
            selected.Configure(1 + context.Steps % 6, writes: context.Steps % 3 == 0, persistent: context.Steps % 4 == 0);
            AttemptRecovery(data, log);
            if (selected.Fired) interruptions++;

            data.Disable();
            log.Disable();
            if (context.Steps % 3 == 0)
            {
                var second = context.Steps % 2 == 0 ? log : data;
                second.Configure(1, writes: false, persistent: true);
                AttemptRecovery(data, log);
                second.Disable();
            }

            using (var recovered = Open(data, log))
            {
                var actual = recovered.GetCollection("rows").Query().OrderBy("_id").ToArray();
                context.Check(actual.Length == baseline.Documents.Length, "Interrupted recovery lost acknowledged rows.");
                for (var i = 0; i < actual.Length; i++)
                    context.Check(BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(baseline.Documents[i])),
                        $"Interrupted recovery changed acknowledged row {i}.");
                recovered.Checkpoint();
            }
            var file = context.StepFile($"recovered-{context.Steps}.db");
            File.WriteAllBytes(file, data.ToArray());
            DatabaseIntegrityVerifier.Verify(context, file);
            context.ObserveNovelty("recovery-failure", selected.Writes, selected.Persistent, selected.Fired);
        }
        if (context.Steps >= 6) context.Check(interruptions > 0, "Recovery campaign did not trigger an injected failure.");
        context.Metrics["recoveryInterruptions"] = interruptions;
        return Task.CompletedTask;
    }

    private static (byte[] Data, byte[] Log, BsonDocument[] Documents) CreateDirtyState(FuzzContext context)
    {
        using var data = new MemoryStream();
        using var log = new MemoryStream();
        var documents = Enumerable.Range(1, 6 + context.Steps % 8).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = context.Seed ^ id, ["payload"] = new byte[1000 + id * 711]
        }).ToArray();
        using (var db = Open(data, log))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "value");
            db.CheckpointSize = 0;
            db.BeginTrans();
            rows.InsertBulk(documents);
            db.Commit();
        }
        return (data.ToArray(), log.ToArray(), documents);
    }

    private static void AttemptRecovery(FaultStream data, FaultStream log)
    {
        LiteDatabase db = null;
        try
        {
            data.Position = 0;
            log.Position = 0;
            db = Open(data, log);
            _ = db.GetCollection("rows").Count();
            db.Checkpoint();
        }
        catch (IOException) { }
        finally
        {
            try { db?.Dispose(); }
            catch (IOException) { }
        }
    }

    private static LiteDatabase Open(Stream data, Stream log) => new(data, logStream: log);

    private sealed class FaultStream : MemoryStream
    {
        private int _remaining = int.MaxValue;
        private bool _enabled;
        internal bool Writes { get; private set; }
        internal bool Persistent { get; private set; }
        internal bool Fired { get; private set; }

        internal FaultStream(byte[] bytes) { base.Write(bytes); Position = 0; }
        internal void Configure(int operation, bool writes, bool persistent)
        {
            _remaining = operation; Writes = writes; Persistent = persistent; Fired = false; _enabled = true;
        }
        internal void Disable() => _enabled = false;
        private void Probe(bool write)
        {
            if (!_enabled || write != Writes || --_remaining > 0) return;
            Fired = true;
            if (!Persistent) _enabled = false;
            throw new IOException("Injected recovery stream failure.");
        }
        public override int Read(byte[] buffer, int offset, int count) { Probe(false); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Probe(false); return base.Read(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Probe(true); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Probe(true); base.Write(buffer); }
        public override void Flush() { Probe(true); base.Flush(); }
    }
}
