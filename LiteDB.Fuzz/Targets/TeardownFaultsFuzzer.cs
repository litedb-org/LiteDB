using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// The teardown step-fault sweep under random prior state (docs/teardown-sweep.md). Each step takes
/// the next two registered teardown drivers (round robin from a random offset, so a campaign of 30
/// steps runs every one of up to 60 drivers), draws for each a prior state within what it
/// tolerates (document count, readers and a pending transaction on other threads, a spilled sort, an
/// upload, encryption, a Shared peer) and runs it unarmed, then runs one of them once more with one
/// random (step site, occurrence, model) fault drawn from the sites its baseline reached.
/// </summary>
/// <remarks>
/// Oracles (inside <see cref="TeardownSweep.Run"/>): FaultReached and FaultDisposed against the path's
/// declared disposition, ConnectionClean for every disposed connection, latched Ownership (Shared),
/// Quiescent at scenario end, Durable on a cold reopen, ScratchLive while a spilled Direct reader is
/// open, leaked page buffers. A violation a skip excuses (its own step's obligations) passes; a match of
/// a registered <see cref="TeardownKnownFindings"/> entry is recorded in <c>teardown.jsonl</c> and passes
/// unless <c>LITEDB_FUZZ_STRICT_KNOWN=1</c>; anything else fails as <c>TEARDOWN_FAULTS_&lt;PATH&gt;_&lt;KIND&gt;</c>.
/// Randomness is drawn only for the choices; case outcomes go to <c>teardown.jsonl</c>, never to the trace.
/// Evidence class: Direct cases are controlled (the fault fires at a named site); Shared cases involve
/// holder threads whose interleaving is native (class 2), so their records keep the full history.
/// </remarks>
internal sealed class TeardownFaultsFuzzer : IFuzzTarget
{
    private static readonly int[] DocumentCounts = { 5, 20, 60 };
    // Drivers per step: a smoke of 30 steps then runs every one of up to 60 drivers' baselines. Constant, so a
    // shorter (minimized) campaign replays a prefix of the same drivers. The smoke's reachability gate reports a
    // step no baseline reached when the driver list outgrows it.
    private const int DriversPerStep = 2;
    // A case builds a database, runs participants bounded at 20 s each, and waits up to ~1.5 s for idle owner threads.
    private static readonly TimeSpan CaseDeadline = TimeSpan.FromSeconds(60);

    public string Name => "teardown-faults";
    public string Description => "Teardown step-fault sweep (skip / fail-inside at every registered teardown step) under random prior state.";

    public Task RunAsync(FuzzContext context)
    {
        var drivers = TeardownDrivers.All.Where(driver => driver.NotApplicable == null).ToArray();
        var offset = context.Random.Next(drivers.Length);
        var root = Path.Combine(context.DirectoryPath, "teardown");
        Directory.CreateDirectory(root);
        var strict = Environment.GetEnvironmentVariable("LITEDB_FUZZ_STRICT_KNOWN") == "1";
        // A skip fault leaves page buffers in use by design; count them instead of failing the host.
        using var leaks = TeardownSweep.CountLeakedBuffers();
        using var records = new StreamWriter(Path.Combine(context.DirectoryPath, "teardown.jsonl"));
        var perStep = DriversPerStep;
        while (context.Next())
        {
            var baselines = new List<(TeardownDriver Driver, TeardownPrior Prior, TeardownRunResult Result)>();
            for (var slot = 0; slot < perStep; slot++)
            {
                var driver = drivers[(offset + (context.Steps - 1) * perStep + slot) % drivers.Length];
                var prior = Prior(context.Random, driver);
                var baseline = context.Deadline("TeardownBaseline",
                    () => TeardownSweep.Run(new TeardownCaseSpec { Driver = driver }, prior, root, isolated: true), driver.Id, CaseDeadline);
                Record(records, context, baseline);
                Judge(context, baseline, strict);
                baselines.Add((driver, prior, baseline));
            }
            var chosen = baselines[context.Random.Next(baselines.Count)];
            var pick = context.Random.Next(int.MaxValue);
            // A Shared driver's visits interleave its holder and caller threads natively (class 2), so their order
            // differs between runs of one seed. Pick from a stable order: the same reached set gives the same case.
            var cases = TeardownSweepPlan.Cases(chosen.Driver, chosen.Result.Visits, TeardownSweepScope.Full)
                .OrderBy(item => item.Step, StringComparer.Ordinal).ThenBy(item => item.Model).ThenBy(item => item.Occurrence).ToList();
            context.Trace("teardown", new { drivers = baselines.Select(item => item.Driver.Id).ToArray(), driver = chosen.Driver.Id,
                prior = chosen.Prior.ToString(), sites = cases.Count });
            if (cases.Count == 0) continue;
            var spec = cases[pick % cases.Count];
            context.Trace("teardown-case", new { site = spec.Step, occurrence = spec.Occurrence, model = spec.ModelName });
            var result = context.Deadline("TeardownCase", () => TeardownSweep.Run(spec, chosen.Prior, root, isolated: true), chosen.Driver.Id, CaseDeadline);
            if (result.Fired && spec.Model == FaultModel.Skip) Reachability.Sometimes("situation:teardown-faults-skip-fired");
            if (result.Fired && spec.Model == FaultModel.FailInside) Reachability.Sometimes("situation:teardown-faults-fail-inside-fired");
            context.Oracles.WriteFault(spec.Step, null, result.Fired, null, null);
            if (result.Fired)
                context.Oracles.WriteFault(null, chosen.Driver.Path, true, TeardownPathRegistry.Find(chosen.Driver.Path).Declared, result.Observed);
            Record(records, context, result);
            Judge(context, result, strict);
            context.ObserveNovelty("teardown", chosen.Driver.Id, spec.Step, spec.ModelName, result.Violations.Count);
        }
        context.Metrics["teardownDrivers"] = drivers.Length;
        return Task.CompletedTask;
    }

    /// <summary>A prior state within what <paramref name="driver"/> tolerates (its defaults' true flags).</summary>
    private static TeardownPrior Prior(Random random, TeardownDriver driver)
    {
        var tolerated = driver.Defaults();
        var prior = new TeardownPrior
        {
            Documents = DocumentCounts[random.Next(DocumentCounts.Length)],
            PendingTransaction = tolerated.PendingTransaction && random.Next(2) == 0,
            OpenReader = tolerated.OpenReader && random.Next(2) == 0,
            SpilledSort = tolerated.SpilledSort && random.Next(2) == 0,
            Upload = random.Next(3) == 0,
            Peer = driver.Mode == TeardownMode.Shared && tolerated.Peer && random.Next(2) == 0,
            Encrypted = tolerated.Encrypted || random.Next(3) == 0
        };
        driver.Require(prior);
        return prior;
    }

    private static void Judge(FuzzContext context, TeardownRunResult result, bool strict)
    {
        if (result.Unexpected.Count == 0) return;
        if (result.KnownFinding != null)
        {
            context.Metrics["teardownKnownFindings"] = (context.Metrics.TryGetValue("teardownKnownFindings", out var known) ? (int)known : 0) + 1;
            if (!strict) return;
        }
        var kind = TeardownRunResult.KindOf(result.Unexpected[0]);
        throw new FuzzFailureException($"TEARDOWN_FAULTS_{FuzzOracles.Safe(result.Spec.Driver.Path)}_{FuzzOracles.Safe(kind)}",
            result.ToString());
    }

    private static void Record(StreamWriter records, FuzzContext context, TeardownRunResult result)
    {
        records.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            target = context.Target, step = context.Steps, driver = result.Spec.Driver.Id, prior = result.Prior.ToString(),
            site = result.Spec.Step, occurrence = result.Spec.Occurrence, model = result.Spec.Baseline ? "baseline" : result.Spec.ModelName,
            fired = result.Fired, firedOnThread = result.FiredOnThread, observed = result.Observed.ToString(), thrown = result.Thrown,
            violations = result.Violations, unexpected = result.Unexpected, knownFinding = result.KnownFinding,
            visits = result.Visits.Length, elapsedMs = Math.Round(result.ElapsedMs, 1)
        }));
        records.Flush();
    }
}
