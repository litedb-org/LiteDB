namespace LiteDB.Fuzz.Targets;

internal sealed class OracleSelfTestFuzzer : IFuzzTarget
{
    public string Name => "oracle-selftest";
    public string Description => "Mutation tests that prove core fuzz oracles reject controlled acknowledged-loss, atomicity, cache, index, snapshot, and vector faults.";

    public Task RunAsync(FuzzContext context)
    {
        var killed = 0;
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
            context.ObserveNovelty("oracle-mutation", killed);
        }
        context.Check(killed == context.Steps * 8, "A controlled harness mutation survived its oracle.");
        context.Metrics["controlledMutationsKilled"] = killed;
        return Task.CompletedTask;
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
