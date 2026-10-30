using LiteDB.Engine;
using LiteDB.Tests.Safety;
using Xunit;

namespace LiteDB.Fuzz.Tests;

/// <summary>
/// The ownership oracle (a protected Shared core active or tearing down keeps the writer mutex;
/// releasing it implies the core's teardown completed) must stay silent through ordinary Shared
/// work and fire when ownership is released while a core is still closing.
/// </summary>
[Collection(OracleSelfTestCollection.Name)]
public sealed class OwnershipOracle_Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litedb-ownership-" + Guid.NewGuid().ToString("N"));

    public OwnershipOracle_Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Ordinary_shared_work_keeps_every_core_under_its_protection()
    {
        using var context = this.Context();
        context.Oracles.WatchOwnership();
        var file = Path.Combine(_root, "ordinary.db");
        var db = new LiteDatabase($"Filename={file};Connection=shared");
        var rows = db.GetCollection("rows");
        rows.Insert(Enumerable.Range(1, 200).Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('o', 3000) }));
        context.Ownership(db, "after a scoped bulk insert");

        Assert.True(db.BeginTrans());
        rows.Update(new BsonDocument { ["_id"] = 1, ["payload"] = "changed" });
        context.Ownership(db, "inside an explicit transaction");
        Assert.True(db.Commit());

        // A thread iterating a leased reader that writes pins the engine on a holder thread.
        using (var reader = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator())
        {
            Assert.True(reader.MoveNext());
            rows.Upsert(new BsonDocument { ["_id"] = 500 });
            context.Ownership(db, "while a pin holds the engine");
            while (reader.MoveNext()) { }
        }
        var peer = new LiteDatabase($"Filename={file};Connection=shared");
        peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 600 });
        peer.Dispose();
        db.Checkpoint();
        db.Rebuild();
        db.Dispose();
        context.ConnectionClean(db);
        context.ConnectionClean(peer, "PeerDispose");
        context.Quiescent(file);
        Assert.Empty(context.Oracles.WatchOwnership().TakeAll());
        Assert.True((int)context.Metrics["ownershipChecks"] >= 3);
    }

    [Fact]
    public void Releasing_ownership_while_a_core_is_closing_fails_the_ownership_oracle()
    {
        using var context = this.Context();
        var monitor = context.Oracles.WatchOwnership();
        var file = Path.Combine(_root, "early.db");
        using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        using var data = new CallbackStream(file);
        using var log = new CallbackStream(Path.Combine(_root, "early-log.db"));
        var connection = new SharedEngine(new EngineSettings { Filename = file, DataStream = data, LogStream = log });
        OwnershipViolation pointCheck = null;
        Exception callbackError = null;
        try
        {
            data.Arm(() =>
            {
                try
                {
                    // The broken state: the connection gives up its writer mutex mid-teardown.
                    OracleSelfTestHooks.ReleaseOwnershipEarly(connection);
                    pointCheck = monitor.Evaluate(connection);
                }
                catch (Exception error) { callbackError = error; }
            });
            // Enough WAL pages that closing the operation's core checkpoints into the data stream.
            connection.Insert("rows", Enumerable.Range(100, 60).Select(id =>
                new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 4000) }).ToArray(), BsonAutoId.Int32);
        }
        finally
        {
            try { connection.Dispose(); }
            catch (Exception) { /* the broken state may leave the connection inconsistent */ }
        }
        Assert.Null(callbackError);
        Assert.True(data.Fired, "the core close must have written through the armed stream");
        Assert.NotNull(pointCheck);
        Assert.Equal("CORE_WITHOUT_PROTECTION", pointCheck.Kind);
        var failure = Assert.Throws<FuzzFailureException>(() => context.Oracles.ThrowLatched());
        Assert.Equal("OWNERSHIP_ORACLE_SELFTEST_RELEASED_BEFORE_TEARDOWN", failure.FailureId);
        Assert.Contains("closing", failure.Message, StringComparison.Ordinal);
    }

    private FuzzContext Context() =>
        new("oracle-selftest", 1, 1, null, Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A caller stream whose next write runs a callback first (inside the engine's I/O).</summary>
    private sealed class CallbackStream : FileStream
    {
        private Action _onWrite;

        internal CallbackStream(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete) { }

        internal bool Fired { get; private set; }

        internal void Arm(Action callback) => Volatile.Write(ref _onWrite, callback);

        public override void Write(byte[] array, int offset, int count)
        {
            this.Fire();
            base.Write(array, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.Fire();
            base.Write(buffer);
        }

        private void Fire()
        {
            var callback = Interlocked.Exchange(ref _onWrite, null);
            if (callback == null) return;
            this.Fired = true;
            callback();
        }
    }
}
