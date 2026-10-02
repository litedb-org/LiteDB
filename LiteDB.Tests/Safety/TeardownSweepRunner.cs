using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Tests.Safety
{
    /// <summary>The outcome of sweeping one driver: its baseline, its cases, and anything that stopped the sweep.</summary>
    internal sealed class TeardownDriverSweep
    {
        public TeardownDriver Driver { get; set; }
        public TeardownRunResult Baseline { get; set; }
        public List<TeardownRunResult> Cases { get; } = new List<TeardownRunResult>();
        public List<string> Problems { get; } = new List<string>();
        public string NotApplicable { get; set; }
        public IEnumerable<TeardownRunResult> All => this.Baseline == null ? this.Cases : new[] { this.Baseline }.Concat(this.Cases);
        public IEnumerable<TeardownRunResult> Failed => this.All.Where(result => !result.Passed);
    }

    /// <summary>
    /// Sweeps a driver: baseline, then every planned case. Cases run in parallel batches on separate
    /// database files (a Shared case waits about a second for its connection's idle mutex owner thread
    /// to exit); the process-wide checks (LiteDB threads, page buffers finalized in use) then run once
    /// per batch, and if either fires the batch is rerun one case at a time so each is attributed.
    /// Skip cases whose step excuses leaked page buffers form their own batch, so a leak they are
    /// allowed never forces the others into a sequential rerun.
    /// </summary>
    internal static class TeardownSweepRunner
    {
        /// <summary>
        /// Concurrent cases (Shared cases twice as many). Not scaled down with the core count: a Shared
        /// case spends most of its time waiting for idle holder threads to exit, a Direct case on small
        /// file I/O. 1 runs every case alone.
        /// </summary>
        public static int Parallelism { get; set; } = 8;

        private static readonly ConcurrentDictionary<string, Lazy<TeardownRunResult>> _defaultBaselines =
            new ConcurrentDictionary<string, Lazy<TeardownRunResult>>(StringComparer.Ordinal);

        /// <summary>
        /// The unarmed run of <paramref name="driver"/> under its default prior state, run once per process:
        /// the xUnit sweep derives its cases from it and the step coverage check reads its visits.
        /// </summary>
        public static TeardownRunResult DefaultBaseline(TeardownDriver driver) =>
            _defaultBaselines.GetOrAdd(driver.Id, _ => new Lazy<TeardownRunResult>(() =>
            {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litedb-teardown-sweep", "baselines");
                System.IO.Directory.CreateDirectory(root);
                return RunBaseline(driver, driver.Defaults(), root);
            })).Value;

        private static TeardownRunResult RunBaseline(TeardownDriver driver, TeardownPrior prior, string root)
        {
            using (TeardownSweep.CountLeakedBuffers())
            {
                // Buffers an earlier test left for the finalizer must not be charged to this baseline.
                TeardownSweep.CollectLeaks();
                return TeardownSweep.Run(new TeardownCaseSpec { Driver = driver }, prior, root, isolated: true);
            }
        }

        /// <param name="baseline">The driver's unarmed run under <paramref name="prior"/>, if already made; run here when null.</param>
        public static TeardownDriverSweep Sweep(TeardownDriver driver, TeardownPrior prior, string root, TeardownSweepScope scope,
            Action<string> log = null, TeardownRunResult baseline = null)
        {
            var sweep = new TeardownDriverSweep { Driver = driver };
            if (driver.NotApplicable != null)
            {
                sweep.NotApplicable = driver.NotApplicable;
                return sweep;
            }
            sweep.Baseline = baseline ?? RunBaseline(driver, prior, root);
            log?.Invoke(sweep.Baseline.ToString());
            using (TeardownSweep.CountLeakedBuffers())
            {
                foreach (var step in sweep.Baseline.Visits.Select(visit => visit.Step).Distinct())
                    if (TeardownStepCatalog.Find(step) == null)
                        sweep.Problems.Add($"step {step} has no TeardownStepCatalog entry (state what fails there and what a skip leaves)");
                if (sweep.Baseline.Visits.Length == 0)
                    sweep.Problems.Add($"driver {driver.Id} reached no teardown step: it does not drive its path");
                if (!sweep.Baseline.Passed || sweep.Problems.Count > 0) return sweep;

                var cases = TeardownSweepPlan.Cases(driver, sweep.Baseline.Visits, scope);
                if (Parallelism <= 1)
                {
                    sweep.Cases.AddRange(cases.Select(spec => Log(TeardownSweep.Run(spec, prior, root, isolated: true), log)));
                    return sweep;
                }
                var width = driver.Mode == TeardownMode.Shared ? 2 * Parallelism : Parallelism;
                var mayLeak = cases.Where(ExcusesLeaks).ToArray();
                var others = cases.Where(spec => !ExcusesLeaks(spec)).ToArray();
                var results = RunBatch(mayLeak, prior, root, log, width, checkLeaks: false)
                    .Concat(RunBatch(others, prior, root, log, width, checkLeaks: true))
                    .ToDictionary(result => result.Spec);
                sweep.Cases.AddRange(cases.Select(spec => results[spec]));
            }
            return sweep;
        }

        /// <summary>A skip whose step may leave page buffers in use: a leak in its batch is excused, not attributed.</summary>
        private static bool ExcusesLeaks(TeardownCaseSpec spec) =>
            spec.Model == FaultModel.Skip && (TeardownStepCatalog.Find(spec.Step)?.SkipLeaves ?? new string[0]).Contains(TeardownStepCatalog.LeakedPages);

        private static IEnumerable<TeardownRunResult> RunBatch(IReadOnlyList<TeardownCaseSpec> cases, TeardownPrior prior, string root,
            Action<string> log, int width, bool checkLeaks)
        {
            if (cases.Count == 0) return new TeardownRunResult[0];
            var results = new TeardownRunResult[cases.Count];
            using (var gate = new SemaphoreSlim(width))
            {
                var tasks = cases.Select((spec, index) => Task.Run(() =>
                {
                    gate.Wait();
                    try { results[index] = TeardownSweep.Run(spec, prior, root, isolated: false); }
                    finally { gate.Release(); }
                })).ToArray();
                Task.WaitAll(tasks);
            }
            var leaked = TeardownSweep.CollectLeaks();
            var threads = QuiescentProbe.Evaluate(System.IO.Path.Combine(root, "batch-threads.db")).Violations
                .Where(item => item.StartsWith("threads", StringComparison.Ordinal)).ToArray();
            if ((leaked == 0 || !checkLeaks) && threads.Length == 0)
            {
                foreach (var result in results) log?.Invoke(result.ToString());
                return results;
            }
            log?.Invoke($"batch: {leaked} leaked page buffer(s), {string.Join("; ", threads)}; rerunning one case at a time");
            return cases.Select(spec => Log(TeardownSweep.Run(spec, prior, root, isolated: true), log)).ToArray();
        }

        private static TeardownRunResult Log(TeardownRunResult result, Action<string> log)
        {
            log?.Invoke(result.ToString());
            return result;
        }
    }
}
