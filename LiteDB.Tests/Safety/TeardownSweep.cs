using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Tests.Safety
{
    /// <summary>One sweep case: a driver, and the fault it arms (none for the baseline).</summary>
    internal sealed class TeardownCaseSpec
    {
        public TeardownDriver Driver { get; set; }
        public string Step { get; set; }
        public FaultModel Model { get; set; }
        public int Occurrence { get; set; }
        /// <summary>
        /// Whether the path's declared disposition applies to this fault: true for the path's own steps, and
        /// for every step when the path has no step markers of its own (it delegates its whole teardown, as
        /// LiteEngine.Dispose does to Close). For another path's step reached during it, only "the entry
        /// surfaced the fault or its primary error, never a replacement" is checked here; that step's own
        /// path judges its declaration in its own driver.
        /// </summary>
        public bool JudgeDeclaration { get; set; } = true;
        public bool Baseline => this.Step == null;
        public TeardownStepSite Site => this.Model == FaultModel.Skip ? TeardownStepSite.Before : TeardownStepSite.After;
        public string ModelName => this.Model == FaultModel.Skip ? "skip" : "fail-inside";

        public override string ToString() => this.Baseline ? this.Driver.Id + " baseline"
            : $"{this.Driver.Id} {this.ModelName} {this.Step}#{this.Occurrence}";
    }

    /// <summary>What one case observed and how it was classified.</summary>
    internal sealed class TeardownRunResult
    {
        public TeardownCaseSpec Spec { get; set; }
        public TeardownPrior Prior { get; set; }
        public TeardownVisit[] Visits { get; set; } = new TeardownVisit[0];
        public bool Fired { get; set; }
        public string FiredOnThread { get; set; }
        public FaultDisposition Observed { get; set; }
        public string Thrown { get; set; }
        /// <summary>Every violation, as <c>&lt;oracle&gt;.&lt;kind&gt;: detail</c>.</summary>
        public List<string> Violations { get; } = new List<string>();
        /// <summary>Violations the fault's model does not excuse (a skip excuses its step's own obligations).</summary>
        public List<string> Unexpected { get; } = new List<string>();
        /// <summary>The registered known finding that explains every unexpected violation, if any.</summary>
        public string KnownFinding { get; set; }
        public double ElapsedMs { get; set; }
        public bool Passed => this.Unexpected.Count == 0 || this.KnownFinding != null;

        public static string KindOf(string violation)
        {
            var colon = violation.IndexOf(':');
            return colon < 0 ? violation : violation.Substring(0, colon);
        }

        public override string ToString() =>
            $"{this.Spec} [{this.Prior}] fired={this.Fired} observed={this.Observed} thrown={this.Thrown ?? "-"}" +
            (this.Unexpected.Count == 0 ? " clean" : this.KnownFinding != null ? $" known={this.KnownFinding}" : " FAILED") +
            string.Concat(this.Violations.Select(item => Environment.NewLine + "    " + item));
    }

    /// <summary>
    /// The teardown step-fault sweep: runs a driver once unarmed (the baseline, which also discovers the
    /// step sites its path reaches), then once per (step, occurrence, model) with that one fault armed,
    /// and checks after each run: FaultReached, FaultDisposed against the path's declared disposition,
    /// ConnectionClean for every disposed connection, latched Ownership violations (Shared), Quiescent at
    /// scenario end, Durable on a cold reopen and leaked page buffers. See docs/teardown-sweep.md.
    /// </summary>
    internal static class TeardownSweep
    {
        private static int _leaks;
        private static int _hooked;

        /// <summary>
        /// Count page buffers finalized while in use instead of letting the TESTING assertion end the
        /// process (a skip fault leaves buffers by design). Keep the scope until finalizers have run.
        /// </summary>
        public static IDisposable CountLeakedBuffers()
        {
            if (Interlocked.Increment(ref _hooked) == 1) PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref _leaks);
            return new Unhook();
        }

        /// <summary>Collect garbage and return the page buffers finalized in use meanwhile.</summary>
        public static int CollectLeaks()
        {
            var before = Volatile.Read(ref _leaks);
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return Volatile.Read(ref _leaks) - before;
        }

        /// <summary>Run one case in a fresh directory below <paramref name="root"/>.</summary>
        /// <param name="isolated">True when no other case runs concurrently: process-wide checks (threads, leaks) apply.</param>
        public static TeardownRunResult Run(TeardownCaseSpec spec, TeardownPrior prior, string root, bool isolated)
        {
            // A fresh thread per case: a skipped release can leave a native mutex owned by, and thread-static
            // state (a scoped ownership count) set on, the thread that ran the case. Both end with that thread.
            TeardownRunResult result = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { result = RunOnThisThread(spec, prior, root, isolated); }
                catch (Exception failure) { error = failure; }
            }) { IsBackground = true, Name = "teardown-sweep case" };
            thread.Start();
            if (!thread.Join(CaseBound))
            {
                // A hang is a finding, not a harness error: report it and leave the blocked (background) thread behind.
                var hung = new TeardownRunResult { Spec = spec, Prior = prior, ElapsedMs = CaseBound.TotalMilliseconds };
                hung.Violations.Add($"deadline.case: the case did not finish within {CaseBound.TotalSeconds:F0} s (its thread is still blocked)");
                Classify(hung);
                return hung;
            }
            if (error != null) throw new InvalidOperationException($"Teardown case {spec} failed in the harness.", error);
            if (isolated)
            {
                // Only now is nothing of the case reachable (its thread ended), so its buffers finalize here.
                var leaked = CollectLeaks();
                if (leaked > 0)
                {
                    result.Violations.Add($"{TeardownStepCatalog.LeakedPages}: {leaked} page buffer(s) finalized while still in use");
                    Classify(result);
                }
            }
            return result;
        }

        /// <summary>The longest a case may take: participants are bounded at 20 s, idle owners exit within ~1.5 s.</summary>
        public static readonly TimeSpan CaseBound = TimeSpan.FromSeconds(55);

        private static TeardownRunResult RunOnThisThread(TeardownCaseSpec spec, TeardownPrior prior, string root, bool isolated)
        {
            var path = TeardownPathRegistry.Find(spec.Driver.Path)
                ?? throw new InvalidOperationException($"Driver {spec.Driver.Id} names no registered teardown path.");
            var result = new TeardownRunResult { Spec = spec, Prior = prior };
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 12));
            System.IO.Directory.CreateDirectory(directory);
            var clock = Stopwatch.StartNew();
            var threadsBefore = isolated ? (QuiescentProbe.LiteDbThreads(new List<string>())?.Length ?? 0) : 0;
            var scenario = new TeardownScenario();
            if (!spec.Baseline)
            {
                scenario.Arm(spec.Step, spec.Site, spec.Occurrence, () => TeardownStepCatalog.Fault(spec.Step, spec.Model, spec.Occurrence));
            }
            var c = new TeardownCase(scenario, directory, prior);
            using (TeardownSteps.Begin(scenario))
            using (c.Ownership = spec.Driver.Mode == TeardownMode.Shared ? new OwnershipMonitor(() => TeardownSteps.Current == scenario) : null)
            {
                try { spec.Driver.Drive(c); }
                catch (Exception error) { result.Violations.Add("driver: the scenario failed outside its entry: " + Describe(error)); }
                foreach (var stop in c.Cleanup)
                {
                    try { stop(); }
                    catch (Exception error) { result.Violations.Add("participant: stopping a participant failed: " + Describe(error)); }
                }
                result.Visits = scenario.Visits();
                result.Fired = scenario.Fired != null;
                result.FiredOnThread = scenario.FiredAt?.Thread.ToString();
                result.Thrown = c.Thrown == null ? null : c.Thrown.GetType().Name + ": " + c.Thrown.Message;
                if (!c.Invoked) result.Violations.Add("driver: the driver never invoked its entry");
                Evaluate(path, spec, c, scenario, result, isolated ? threadsBefore : int.MaxValue);
            }
            Classify(result);
            result.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            TryDelete(directory);
            return result;
        }

        private static void Evaluate(TeardownPathInfo path, TeardownCaseSpec spec, TeardownCase c, TeardownScenario scenario,
            TeardownRunResult result, int allowedThreads)
        {
            var v = result.Violations;
            if (!spec.Baseline)
            {
                if (scenario.Fired == null) v.Add($"reached: the armed {spec.ModelName} fault at {spec.Step}#{spec.Occurrence} never fired");
                else
                {
                    result.Observed = FaultDisposedProbe.Observe(scenario.Fired, c.Thrown, c.Returned, c.Primary);
                    var declared = path.Declared;
                    // With the caller's primary error standing, a discarded fault shows as suppressed behind it.
                    if ((declared & FaultDisposition.Discarded) != 0 && c.Primary != null) declared |= FaultDisposition.SuppressedPreservingPrimary;
                    if (spec.JudgeDeclaration ? (result.Observed & declared) == 0 : result.Observed == FaultDisposition.None)
                        v.Add($"disposition: {path.Name} declares {path.Declared} but disposed of the fault as {Name(result.Observed)}" +
                            (spec.JudgeDeclaration ? "" : " (a nested step's fault was replaced)") +
                            (c.Thrown == null ? "" : " (threw " + Describe(c.Thrown) + ")"));
                }
            }
            else
            {
                if (c.Thrown != null && (c.Primary == null || !FaultDisposedProbe.Carries(c.Thrown, c.Primary, false, 0)))
                    v.Add("baseline: the entry threw without an injected fault: " + Describe(c.Thrown));
                var failures = (c.Returned ?? new Exception[0]).Where(item => !ReferenceEquals(item, c.Primary)).ToArray();
                if (failures.Length > 0)
                    v.Add("baseline: the entry returned failures without an injected fault: " + string.Join("; ", failures.Select(Describe)));
            }
            foreach (var connection in c.Disposed.Distinct())
                foreach (var item in ConnectionCleanProbe.Evaluate(connection, c.Ownership).Violations) v.Add("connection." + item);
            if (c.Ownership != null)
                foreach (var item in c.Ownership.TakeAll()) v.Add("ownership." + item);
            if (c.DatabasePath != null)
            {
                foreach (var item in QuiescentProbe.Evaluate(c.DatabasePath, allowedThreads).Violations) v.Add("quiescent." + item);
                if (c.CheckDurable) Durable(c, v);
            }
            v.AddRange(c.Violations);
        }

        private static void Durable(TeardownCase c, List<string> v)
        {
            try
            {
                var reopen = new ConnectionString
                {
                    Filename = c.DatabasePath, Connection = ConnectionType.Direct, Password = c.Password,
                    AutoRebuild = c.ReopenWithAutoRebuild
                };
                using (var db = new LiteDatabase(reopen))
                    foreach (var item in c.Ledger.Verify(db)) v.Add("durable." + item);
            }
            catch (Exception error) { v.Add("durable.REOPEN_FAILED: the cold reopen failed: " + Describe(error)); }
        }

        /// <summary>Excuse what a skip leaves of its own step; match the rest against the known findings.</summary>
        internal static void Classify(TeardownRunResult result)
        {
            var spec = result.Spec;
            var excused = new HashSet<string>(StringComparer.Ordinal);
            if (!spec.Baseline && spec.Model == FaultModel.Skip)
                foreach (var kind in TeardownStepCatalog.Find(spec.Step)?.SkipLeaves ?? new string[0]) excused.Add(kind);
            result.Unexpected.Clear();
            result.Unexpected.AddRange(result.Violations.Where(item => !excused.Contains(TeardownRunResult.KindOf(item))));
            result.KnownFinding = result.Unexpected.Count == 0 ? null : TeardownKnownFindings.Match(result)?.Id;
        }

        internal static string Describe(Exception error)
        {
            var text = error.GetType().Name + ": " + error.Message;
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                text += " [" + string.Join("; ", aggregate.InnerExceptions.Select(Describe)) + "]";
            return text.Length > 600 ? text.Substring(0, 600) + "..." : text;
        }

        private static string Name(FaultDisposition disposition) => disposition == FaultDisposition.None ? "Replaced" : disposition.ToString();

        private static void TryDelete(string directory)
        {
            try { System.IO.Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Unhook : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) != 0) return;
                if (Interlocked.Decrement(ref _hooked) != 0) return;
                CollectLeaks();
                PageBuffer.FinalizedInUse = null;
            }
        }
    }
}
