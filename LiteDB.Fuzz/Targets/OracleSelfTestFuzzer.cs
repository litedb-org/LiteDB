using LiteDB.Tests.Safety;

namespace LiteDB.Fuzz.Targets;

internal sealed class OracleSelfTestFuzzer : IFuzzTarget
{
    public string Name => "oracle-selftest";
    public string Description => "Mutation tests that prove core fuzz oracles reject controlled acknowledged-loss, atomicity, cache, index, snapshot, vector, swallowed-fault, durability and post-close faults.";

    private const int MutationsPerStep = 12;

    public Task RunAsync(FuzzContext context)
    {
        var killed = 0;
        var closed = context.RegisterFile(Path.Combine(context.DirectoryPath, "closed.db"));
        using (var db = new LiteDatabase(closed)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        using var memory = new LiteDatabase(new MemoryStream());
        while (context.Next())
        {
            killed += Reject(() => FuzzOracle.VerifyAtomicState(context,
                false, false, false, false, "acknowledged commit loss"));
            killed += Reject(() => FuzzOracle.VerifyAtomicState(context,
                true, false, false, true, "mixed transaction branches"));
            killed += Reject(() => FuzzOracle.VerifyCrashMarker(context,
                "3|outer-after-commit", "4|outer-after-commit"));
            killed += Reject(() => FuzzOracle.VerifyBsonValue(context, 20, 10,
                "stale A-B-A parameter cache"));
            killed += Reject(() => FuzzOracle.VerifyDistinct(context, new[] { "A", "a" },
                StringComparer.OrdinalIgnoreCase, "unique collation bypass"));
            killed += Reject(() => FuzzOracle.VerifySequence(context,
                new[] { 1, 3, 2 }, new[] { 1, 2, 3 }, "secondary index corruption"));
            killed += Reject(() => FuzzOracle.VerifySnapshotResult(context, "stale"));
            killed += Reject(() => FuzzOracle.VerifyVectorScore(context, 0d,
                Math.Sqrt(2d), 1e-6, "bad vector score"));
            var injected = new IOException("injected");
            killed += Reject(() => context.FaultDisposed("swallowed", injected, FaultDisposition.Propagated, null));
            killed += Reject(() => context.FaultDisposed("replaced", injected, FaultDisposition.Propagated, new TimeoutException()));
            var lost = new DurableLedger();
            lost.Acknowledge("rows", context.Steps, new BsonDocument { ["_id"] = context.Steps });
            killed += Reject(() => context.Durable(lost, memory, "acknowledged write lost"));
            killed += Reject(() => WhileMutexHeld(closed, () => context.Quiescent(closed, "mutex still held")));
            context.ObserveNovelty("oracle-mutation", killed);
        }
        context.Check(killed == context.Steps * MutationsPerStep, "A controlled harness mutation survived its oracle.");
        context.Metrics["controlledMutationsKilled"] = killed;
        return Task.CompletedTask;
    }

    private static void WhileMutexHeld(string path, Action action)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = QuiescentProbe.OpenMutex(path);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true, Name = "selftest mutex holder" };
        holder.Start();
        held.Wait();
        try { action(); }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    private static int Reject(Action verification)
    {
        try
        {
            verification();
        }
        catch (FuzzFailureException)
        {
            return 1;
        }
        throw new FuzzFailureException("ORACLE_MUTATION_SURVIVED",
            "A controlled mutation survived a real fuzz-target verification path.");
    }
}
