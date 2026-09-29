using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_PromotionAfterFailedCheckpoint;

/// <summary>
/// Defect found in PR #3027 and fixed by the #3051 S09 extraction
/// (branch split/09-durability-protocol; guard: CheckpointFailureWindow_Tests). The first compact write into a v11
/// file (CompactStorage = Compact) promotes it to v12 under the header lock, which a checkpoint holds
/// (its commit lock) with the WAL writer while it syncs the data file. When that sync failed, the
/// known-bad checkpoint stopped the engine only after releasing its locks, and the promotion checked no
/// engine state: a compact insert past its per-document check that waited for the header lock then
/// wrote a v12 header and synced the data file again on the handle whose sync had just failed
/// (fsyncgate: the retry "succeeds"). Fixed: under the WAL writer the promotion throws the failure that
/// stopped the engine, or the recorded one, before it syncs or writes anything (decision 6).
///
/// Black box: a v11 database on caller streams keeps one commit in its WAL (CHECKPOINT = 0), so the
/// checkpoint retires no frame. Reopened with CompactStorage = Compact, an insert on its own thread
/// stores a document as BSON (the first of its shape), then a second of that shape compactly, which
/// promotes the file. The second is a BsonDocument whose indexer holds the thread when the engine reads
/// its _id for compact encoding (after the per-document check, before the promotion). A checkpoint on
/// another thread then syncs the data file (the caller stream's Flush), which releases the insert,
/// waits until it blocks on the header lock and fails with EIO. The device journals every stream
/// operation and, after the failure, answers syncs with success as fsyncgate storage does.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    // HeaderPage.P_FILE_VERSION: the format byte of the data header; v11 (index ordering), v12 (compact).
    private const int FileVersionOffset = 59;
    private const byte IndexVersion = 11;
    private const byte CompactVersion = 12;
    private const int Rows = 20;
    private const int Eio = 5;
    private const string Injected = "injected EIO: the data file's sync failed";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host);
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host)
    {
        var (dataImage, logImage) = Setup();
        host.SendLog($"Setup: a v{dataImage[FileVersionOffset]} data file ({dataImage.Length} bytes) whose WAL keeps one commit ({logImage.Length} bytes)");

        var journal = new Journal();
        using var data = new Device("data", dataImage, journal);
        using var log = new Device("log", logImage, journal);
        var gate = Compact(new GateDocument(), "c1");
        Exception? insertError = null, checkpointError = null;
        var inserted = -1;
        Failure? failure = null;
        Thread inserter, checkpointer;

        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Compact }))
        {
            Require(data.FileVersion == IndexVersion && log.Length > 0, $"the reopened database is v{data.FileVersion} with a {log.Length}-byte WAL");

            // The first document of a shape is stored as BSON; the second of that shape is compact.
            inserter = Background(() => inserted = engine.Insert("compact", new[] { Compact(new BsonDocument(), "c0"), gate }, BsonAutoId.ObjectId),
                error => insertError = error);
            gate.Gated = inserter;
            inserter.Start();
            Require(gate.Reached.Wait(Timeout), "the insert did not reach the compact encoding of its second document");

            checkpointer = Background(() => engine.Checkpoint(), error => checkpointError = error);
            // The checkpoint's first data sync, under its commit (header) lock and WAL writer.
            data.FailSync(checkpointer, () =>
            {
                gate.Release.Set();
                var blocked = SpinWait.SpinUntil(() => gate.Left && (inserter.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout);
                failure = new Failure(journal.Count, blocked, data.ToArray(), log.ToArray());
                throw new IOException(Injected, Eio);
            });
            journal.Arm();
            checkpointer.Start();
            Require(checkpointer.Join(Timeout * 2), "the checkpoint did not return");
            gate.Release.Set();
            Require(inserter.Join(Timeout * 2), "the insert did not return");
        }

        Require(failure != null, $"the checkpoint did not sync the data file (it {(checkpointError == null ? "returned" : "threw " + checkpointError.Message)})");
        host.SendLog($"The checkpoint threw {checkpointError?.GetType().Name}: {checkpointError?.Message}");
        host.SendLog($"The insert that waited on it {(insertError == null ? $"returned {inserted}" : $"threw {insertError.GetType().Name}: {insertError.Message}")}");
        Require(checkpointError != null && checkpointError.ToString().Contains(Injected), "the checkpoint did not fail with the injected data sync failure");
        Require(failure!.InsertBlocked, "the insert did not block on the checkpoint before its data sync failed");
        var id = inserter.ManagedThreadId;
        Require(!journal.Between(0, failure.Index).Any(x => x.Thread == id), "the insert touched the streams before the checkpoint's data sync failed");
        Require(failure.Data[FileVersionOffset] == IndexVersion, $"the data header was v{failure.Data[FileVersionOffset]} when the sync failed");

        var after = journal.Between(failure.Index, journal.Count);
        var version = data.FileVersion;
        host.SendLog($"After the failed data sync: {Describe(after, id, checkpointer.ManagedThreadId)}; the data header is v{version}");

        if (after.Any(x => x.Kind != "read") || version != IndexVersion)
        {
            // The promotion: the header written over the v11 one, then the data file synced again.
            var header = after.FindIndex(x => x.Thread == id && x.Device == "data" && x.Kind == "write" && x.Position == 0);
            Require(header >= 0 && after.Skip(header).Any(x => x.Thread == id && x.Device == "data" && x.Kind == "sync"),
                "the insert did not write the data header and sync the data file after the failure");
            Require(version == CompactVersion, $"the data header is v{version} after the failure, not v{CompactVersion}");
            return (Reproduced, "REPRODUCED: a compact insert that waited on the checkpoint whose data sync failed promoted the file after that failure: " +
                "it wrote a v12 header over the v11 one and synced the data file again on the handle whose sync had just failed, which reported success");
        }

        Require(insertError != null && insertError.ToString().Contains(Injected), "the insert that waited on the failed checkpoint was not refused with its failure");
        Require(data.ToArray().SequenceEqual(failure.Data) && log.ToArray().SequenceEqual(failure.Log), "the streams changed after the failure");
        Reopen(host, data, log);
        return (Fixed, "FIXED: the compact insert that waited on the checkpoint whose data sync failed was refused with that failure: nothing was " +
            "written to or synced on either file after the failed sync, the data header stayed v11, and a reopen has every committed row");
    }

    /// <summary>
    /// A v11 database (CompactStorage = Legacy): rows 1-20 and the compact collection's seed checkpointed
    /// into the data file, row 21 in the WAL (one commit, so the checkpoint retires no frame).
    /// </summary>
    private static (byte[] Data, byte[] Log) Setup()
    {
        using var data = new MemoryStream();
        using var log = new MemoryStream();
        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            db.GetCollection("compact").Insert(new BsonDocument { ["_id"] = "seed" });
            db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(Row));
            db.Checkpoint();
            db.GetCollection("rows").Insert(Row(Rows + 1));
        }
        return (data.ToArray(), log.ToArray());
    }

    private static Thread Background(Action action, Action<Exception> failed) =>
        new Thread(() => { try { action(); } catch (Exception error) { failed(error); } }) { IsBackground = true };

    /// <summary>
    /// A copy of the streams' bytes (what a killed process leaves) holds every committed row and only the
    /// compact collection's seed; it takes compact inserts (promoting it now) and keeps them.
    /// </summary>
    private static void Reopen(ReproHostClient host, Device data, Device log)
    {
        var (dataCopy, logCopy) = (new MemoryStream(), new MemoryStream());
        dataCopy.Write(data.ToArray());
        logCopy.Write(log.ToArray());
        LiteDatabase Open() => new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = dataCopy, LogStream = logCopy, CompactStorage = CompactStorageMode.Compact }));
        static string Compacted(LiteDatabase db) =>
            string.Join(", ", db.GetCollection("compact").FindAll().Select(x => x["_id"].AsString).OrderBy(x => x, StringComparer.Ordinal));
        using (var db = Open())
        {
            var rows = db.GetCollection("rows").FindAll().ToList();
            Require(rows.Select(x => x["_id"].AsInt32).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, Rows + 1)) && rows.All(x => x["payload"] == Row(1)["payload"]),
                $"reopened: rows [{string.Join(", ", rows.Select(x => x["_id"]))}], expected 1-{Rows + 1}");
            Require(Compacted(db) == "seed", $"reopened: the compact collection holds [{Compacted(db)}]");
            db.GetCollection("compact").Insert(Enumerable.Range(2, 3).Select(i => Compact(new BsonDocument(), $"c{i}")));
            host.SendLog($"Reopened: rows 1-{Rows + 1} and the seed; three compact inserts then promoted the file to v{dataCopy.ToArray()[FileVersionOffset]}");
        }
        using (var again = Open())
            Require(Compacted(again) == "c2, c3, c4, seed" && again.GetCollection("rows").Count() == Rows + 1,
                $"reopened again: the compact collection holds [{Compacted(again)}], {again.GetCollection("rows").Count()} rows");
    }

    /// <summary>The stream operations in order, by thread ("the insert: data read, log sync, ...").</summary>
    private static string Describe(List<Op> ops, int inserter, int checkpointer)
    {
        var runs = new List<(int Thread, List<string> Ops)>();
        foreach (var op in ops)
        {
            if (runs.Count == 0 || runs[^1].Thread != op.Thread) runs.Add((op.Thread, new List<string>()));
            runs[^1].Ops.Add($"{op.Device} {op.Kind}");
        }
        string Who(int thread) => thread == inserter ? "the insert" : thread == checkpointer ? "the checkpoint" : $"thread {thread}";
        return runs.Count == 0 ? "no stream operation" : string.Join("; ", runs.Select(x => $"{Who(x.Thread)}: {string.Join(", ", x.Ops)}"));
    }

    private static T Compact<T>(T doc, string id) where T : BsonDocument
    {
        doc["_id"] = id;
        doc["longRepeatedFieldName"] = 1;
        doc["anotherLongRepeatedFieldName"] = "payload";
        doc["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = 1, ["anotherNestedFieldName"] = true };
        return doc;
    }

    private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id, ["payload"] = new string('r', 200) };

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed record Failure(int Index, bool InsertBlocked, byte[] Data, byte[] Log);
    private sealed record Op(string Device, string Kind, long Position, int Thread);

    /// <summary>Every stream operation of both devices once armed, in order.</summary>
    private sealed class Journal
    {
        private readonly List<Op> _ops = new List<Op>();
        private volatile bool _armed;

        internal void Arm() => _armed = true;
        internal int Count { get { lock (_ops) return _ops.Count; } }
        internal List<Op> Between(int from, int to) { lock (_ops) return _ops.Skip(from).Take(to - from).ToList(); }
        internal void Add(string device, string kind, long position) { if (_armed) lock (_ops) _ops.Add(new Op(device, kind, position, Environment.CurrentManagedThreadId)); }
    }

    /// <summary>
    /// A caller stream that journals its reads, writes, truncations and syncs. Armed, the first sync on the
    /// given thread runs the hook, which fails it; later syncs succeed, as a retry on fsyncgate storage does.
    /// </summary>
    private sealed class Device : MemoryStream
    {
        private readonly string _name;
        private readonly Journal _journal;
        private Thread? _failOn;
        private Action? _hook;

        internal Device(string name, byte[] image, Journal journal)
        {
            (_name, _journal) = (name, journal);
            base.Write(image, 0, image.Length);
            Position = 0;
        }

        internal byte FileVersion => ToArray()[FileVersionOffset];
        internal void FailSync(Thread thread, Action hook) => (_failOn, _hook) = (thread, hook);
        private void Note(string kind, long position) => _journal.Add(_name, kind, position);

        public override void Flush()
        {
            if (_failOn == Thread.CurrentThread && _hook is { } hook)
            {
                _hook = null;
                Note("failed sync", Position);
                hook();
            }
            Note("sync", Position);
            base.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) { Note("read", Position); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Note("read", Position); return base.Read(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Note("write", Position); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Note("write", Position); base.Write(buffer); }
        public override void WriteByte(byte value) { Note("write", Position); base.WriteByte(value); }
        public override void SetLength(long value) { Note("truncate", value); base.SetLength(value); }
    }

    /// <summary>
    /// A document whose indexer holds the gated thread the first time it reads a field: the insert reads
    /// the _id for its compact encoding after its per-document engine check and before the promotion.
    /// </summary>
    private sealed class GateDocument : BsonDocument
    {
        internal Thread? Gated;
        internal readonly ManualResetEventSlim Reached = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        internal volatile bool Left;

        public override BsonValue this[string key]
        {
            get
            {
                if (Gated == Thread.CurrentThread && !Reached.IsSet)
                {
                    Reached.Set();
                    Release.Wait(Timeout);
                    Left = true;
                }
                return base[key];
            }
            set => base[key] = value;
        }
    }
}
