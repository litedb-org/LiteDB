using System.Reflection;
using LiteDB.Tests.Safety;
using Xunit;

namespace LiteDB.Fuzz.Tests;

/// <summary>
/// Every invariant oracle must reject a deliberately broken state and accept the unaltered one
/// (validation rules: test the oracle with broken states, not only green runs). Quiescent judges
/// the whole process (LiteDB threads, the mutex), so these tests never run in parallel with other
/// tests that open databases. Ownership has its own class.
/// </summary>
[Collection(OracleSelfTestCollection.Name)]
public sealed class OracleSelfTest_Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litedb-oracles-" + Guid.NewGuid().ToString("N"));

    public OracleSelfTest_Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ConnectionClean_passes_after_dispose_and_fails_for_what_a_connection_still_owns()
    {
        using var context = this.Context();
        var direct = new LiteDatabase(Path.Combine(_root, "direct.db"));
        direct.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        Assert.Equal("CONNECTION_CLEAN_ORACLE_SELFTEST_ENGINE",
            Assert.Throws<FuzzFailureException>(() => context.ConnectionClean(direct, "not disposed")).FailureId);
        direct.Dispose();
        Assert.True(context.ConnectionClean(direct).Clean);

        var shared = new LiteDatabase($"Filename={Path.Combine(_root, "shared.db")};Connection=shared");
        Assert.True(shared.BeginTrans());
        shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        var owned = Assert.Throws<FuzzFailureException>(() => context.ConnectionClean(shared, "transaction still open"));
        Assert.Equal("CONNECTION_CLEAN_ORACLE_SELFTEST_CORES", owned.FailureId);
        Assert.Contains("ownership:", owned.Message, StringComparison.Ordinal);
        shared.Dispose();
        var clean = context.ConnectionClean(shared);
        Assert.False(clean.HolderThread);
        Assert.False(clean.OwnershipHeld);
    }

    [Fact]
    public void Quiescent_fails_on_a_leaked_handle_and_passes_once_it_is_closed()
    {
        var file = this.CreateClosedDatabase("leak.db");
        using var context = this.Context();
        Assert.True(context.Quiescent(file).Clean);

        var leaked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var probe = QuiescentProbe.Evaluate(file);
            if (probe.OpenFds < 0)
            {
                // macOS: no in-process handle listing; the gap must be stated, not hidden.
                Assert.Contains(probe.Gaps, gap => gap.StartsWith("handles:", StringComparison.Ordinal));
                return;
            }
            Assert.Equal("QUIESCENT_ORACLE_SELFTEST_HANDLES",
                Assert.Throws<FuzzFailureException>(() => context.Quiescent(file)).FailureId);
        }
        finally { leaked.Dispose(); }
        Assert.True(context.Quiescent(file).Clean);
        Assert.Contains(File.ReadAllLines(Path.Combine(context.DirectoryPath, FuzzOracleState.QuiescentFile)),
            line => line.Contains("\"clean\":false", StringComparison.Ordinal));
    }

    [Fact]
    public void Quiescent_fails_while_the_database_mutex_is_held_or_scratch_remains()
    {
        var file = this.CreateClosedDatabase("mutex.db");
        using var context = this.Context();
        using (var held = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var holder = new Thread(() =>
            {
                using var mutex = QuiescentProbe.OpenMutex(file);
                mutex.WaitOne();
                held.Set();
                release.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            holder.Start();
            held.Wait();
            try
            {
                Assert.Equal("QUIESCENT_ORACLE_SELFTEST_MUTEX",
                    Assert.Throws<FuzzFailureException>(() => context.Quiescent(file)).FailureId);
            }
            finally
            {
                release.Set();
                holder.Join();
            }
        }
        File.WriteAllBytes(QuiescentProbe.ScratchPath(file), new byte[8192]);
        Assert.Equal("QUIESCENT_ORACLE_SELFTEST_SCRATCH",
            Assert.Throws<FuzzFailureException>(() => context.Quiescent(file)).FailureId);
        File.Delete(QuiescentProbe.ScratchPath(file));
        Assert.True(context.Quiescent(file).Clean);
    }

    [Fact]
    public void Quiescent_fails_when_a_LiteDB_thread_outlives_the_grace_period()
    {
        if (!Directory.Exists("/proc/self/task")) return; // thread names are not enumerable here (stated as a gap)
        var file = this.CreateClosedDatabase("thread.db");
        using var context = this.Context();
        using var stop = new ManualResetEventSlim();
        var leaked = new Thread(() => stop.Wait()) { IsBackground = true, Name = "LiteDB leaked owner" };
        leaked.Start();
        try
        {
            Assert.Equal("QUIESCENT_ORACLE_SELFTEST_THREADS",
                Assert.Throws<FuzzFailureException>(() => context.Quiescent(file)).FailureId);
        }
        finally
        {
            stop.Set();
            leaked.Join();
        }
    }

    [Fact]
    public void Quiescent_waits_past_the_grace_for_an_idle_holder_thread_and_fails_one_that_never_exits()
    {
        if (!Directory.Exists("/proc/self/task")) return; // thread names are not enumerable here (stated as a gap)
        var file = this.CreateClosedDatabase("holder.db");
        // Named like the Shared mutex owner thread, the only LiteDB thread that exits on its own once idle.
        using (var stop = new ManualResetEventSlim())
        {
            var late = new Thread(() => stop.Wait()) { IsBackground = true, Name = "LiteDB shared mutex owner" };
            late.Start();
            using var timer = new Timer(_ => stop.Set(), null, 2500, Timeout.Infinite);
            var result = QuiescentProbe.Evaluate(file, lateBound: TimeSpan.FromSeconds(10));
            late.Join();
            Assert.True(result.Clean, string.Join("; ", result.Violations));
            Assert.True(result.LateThreadExit);
            Assert.True(result.WaitedMs > 2000, $"waited {result.WaitedMs:F0} ms");
        }
        using (var stop = new ManualResetEventSlim())
        {
            var leaked = new Thread(() => stop.Wait()) { IsBackground = true, Name = "LiteDB shared mutex owner" };
            leaked.Start();
            try
            {
                var result = QuiescentProbe.Evaluate(file, lateBound: TimeSpan.FromSeconds(3));
                Assert.Contains(result.Violations, v => v.StartsWith("threads: 1 LiteDB thread(s) after 3000 ms", StringComparison.Ordinal));
                Assert.False(result.LateThreadExit);
            }
            finally
            {
                stop.Set();
                leaked.Join();
            }
        }
    }

    [Fact]
    public void Quiescent_grace_is_the_owner_thread_idle_limit_plus_two_polls()
    {
        var owner = typeof(LiteDatabase).Assembly.GetType("LiteDB.Client.Shared.SharedMutexOwner", throwOnError: true)!;
        var idle = (TimeSpan)owner.GetField("HolderIdle", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var poll = (TimeSpan)owner.GetField("Poll", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(idle + poll + poll + QuiescentProbe.SchedulingAllowance, QuiescentProbe.Grace);
        Assert.Equal(0, QuiescentProbe.IdleThreadCap);
    }

    [Fact]
    public void ScratchLive_holds_for_a_live_spilled_reader_and_fails_when_its_scratch_is_gone()
    {
        using var context = this.Context();
        var spills = 0;
        using (OracleSelfTestHooks.CountSortSpills(() => Interlocked.Increment(ref spills)))
        {
            // Unaltered: the scratch lives while the spilled reader does and is gone after close.
            var kept = Path.Combine(_root, "spill-kept.db");
            using (var db = new LiteDatabase(kept))
            using (var reader = SpilledReader(db))
            {
                Assert.True(spills > 0, "the self-test must spill a sort to disk");
                context.ScratchLive(kept, "spilled reader live");
            }
            context.Quiescent(kept);

            if (OperatingSystem.IsWindows()) return; // an open scratch file cannot be deleted there
            // The defect class: scratch deleted under a live reader.
            var lost = Path.Combine(_root, "spill-lost.db");
            using (var db = new LiteDatabase(lost))
            using (var reader = SpilledReader(db))
            {
                File.Delete(QuiescentProbe.ScratchPath(lost));
                Assert.Equal("SCRATCH_LIVE_ORACLE_SELFTEST",
                    Assert.Throws<FuzzFailureException>(() => context.ScratchLive(lost, "scratch deleted")).FailureId);
            }
        }
    }

    private static IEnumerator<BsonDocument> SpilledReader(LiteDatabase db)
    {
        var rows = db.GetCollection("rows");
        rows.InsertBulk(Enumerable.Range(1, 1500).Select(id => new BsonDocument
        {
            // Sort keys, not documents, fill the in-memory container: long keys force a spill.
            ["_id"] = id, ["key"] = ((id * 7919) % 1500).ToString("D5") + new string('k', 900)
        }));
        var reader = rows.Query().OrderBy("key").ToEnumerable().GetEnumerator();
        Assert.True(reader.MoveNext());
        return reader;
    }

    [Fact]
    public void FaultDisposed_matches_the_declared_contract_and_fails_for_a_swallowed_or_replaced_fault()
    {
        using var context = this.Context();
        var injected = new IOException("injected");
        // Swallowing an unexpected error on a path that declares propagation fails.
        Assert.Equal("FAULT_DISPOSED_ORACLE_SELFTEST_UPLOAD_DISCARDED",
            Assert.Throws<FuzzFailureException>(() => context.FaultDisposed("Upload", injected, FaultDisposition.Propagated, null)).FailureId);
        Assert.Equal("FAULT_DISPOSED_ORACLE_SELFTEST_UPLOAD_REPLACED",
            Assert.Throws<FuzzFailureException>(() =>
                context.FaultDisposed("Upload", injected, FaultDisposition.Propagated, new ObjectDisposedException("engine"))).FailureId);

        context.FaultDisposed("Upload", injected, FaultDisposition.Propagated, injected);
        context.FaultDisposed("Upload", injected, FaultDisposition.Propagated, new InvalidOperationException("wrapped", injected));
        // Upstream LiteEngine.Dispose declares discard: a dropped failure list is its contract.
        context.FaultDisposed("Dispose", injected, FaultDisposition.Discarded, null);
        context.FaultDisposed("Close", injected, FaultDisposition.ReturnedAsFailureList, null, returned: new[] { injected });
        var primary = new TimeoutException("primary");
        var carrier = new TimeoutException("primary with cleanup error");
        carrier.Data["cleanup"] = injected;
        context.FaultDisposed("Cleanup", injected, FaultDisposition.RecordedAsCleanupError, carrier);
        context.FaultDisposed("Cleanup", injected, FaultDisposition.SuppressedPreservingPrimary, primary, primary: primary);
        context.FaultDisposed("Retry", injected, FaultDisposition.Retried, null, retried: true);
        Assert.Throws<FuzzFailureException>(() => context.FaultDisposed("Dispose", injected, FaultDisposition.Discarded, injected));
        context.FaultDisposed("Upload", null, FaultDisposition.Propagated, null);
        Assert.Equal(10, File.ReadAllLines(Path.Combine(context.DirectoryPath, FuzzOracleState.FaultsFile)).Length);
    }

    [Fact]
    public void FaultReached_fails_a_scenario_whose_required_fault_never_fired_and_models_stay_distinct()
    {
        using var context = this.Context();
        Assert.Equal("FAULT_NOT_REACHED_ORACLE_SELFTEST_UPLOAD_SOURCE",
            Assert.Throws<FuzzFailureException>(() => context.FaultReached("upload-source", null, required: true)).FailureId);
        Assert.False(context.FaultReached("optional", null));

        var ran = 0;
        var inside = new FaultInjection("inside", FaultModel.FailInside);
        Assert.Throws<IOException>(() => inside.Run(() => ran++, () => new IOException("inside")));
        var skip = new FaultInjection("skip", FaultModel.Skip);
        Assert.Throws<IOException>(() => skip.Run(() => ran++, () => new IOException("skip")));
        Assert.Equal(1, ran); // fail-inside ran the action; skip prevented it
        Assert.True(context.FaultReached("inside", inside.Injected, required: true));
        skip.Run(() => ran++, () => new IOException("not again"));
        Assert.Equal(2, ran);
    }

    [Fact]
    public void Durable_fails_for_a_lost_acknowledged_effect_or_a_visible_aborted_one()
    {
        var file = Path.Combine(_root, "durable.db");
        using (var db = new LiteDatabase(file))
        {
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "rolled back" });
        }
        using var context = this.Context();
        using var cold = new LiteDatabase(file);

        var healthy = new DurableLedger();
        healthy.Acknowledge("rows", 1, new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
        healthy.Abort("rows", 3, null);
        context.Durable(healthy, cold, "reopen");

        var lost = new DurableLedger();
        lost.Acknowledge("rows", 4, new BsonDocument { ["_id"] = 4, ["value"] = "acknowledged" });
        Assert.Equal("DURABLE_ORACLE_SELFTEST_ACKNOWLEDGED_LOST",
            Assert.Throws<FuzzFailureException>(() => context.Durable(lost, cold, "reopen")).FailureId);

        var visible = new DurableLedger();
        visible.Abort("rows", 2, null);
        Assert.Equal("DURABLE_ORACLE_SELFTEST_ABORTED_VISIBLE",
            Assert.Throws<FuzzFailureException>(() => context.Durable(visible, cold, "reopen")).FailureId);
    }

    private FuzzContext Context() =>
        new("oracle-selftest", 1, 1, null, Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")));

    private string CreateClosedDatabase(string name)
    {
        var file = Path.Combine(_root, name);
        using (var db = new LiteDatabase(file)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OracleSelfTestCollection
{
    public const string Name = "Process-wide oracle self-tests";
}
