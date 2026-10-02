using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Net-proof campaign driver for the parallel property with the handle access kind (historical adapter).
    /// Runs consecutive case seeds until the first failure (which the runner replays, shrinks and
    /// classifies) or until the time budget ends, then keeps evaluating further seeds without investigation
    /// to measure the per-case failure rate. Fails when any case failed. Environment:
    /// LITEDB_PBT_MODE (Direct|Shared), LITEDB_PBT_MAX_SUFFIX_THREADS (default 3),
    /// LITEDB_PBT_TIME_BUDGET_S (default 20), LITEDB_PBT_BASE_SEED (default 1),
    /// LITEDB_PBT_ACCESS_KINDS (default ordinary,legacy,handle), LITEDB_PBT_ARTIFACT_DIR (summary + evidence).
    /// </summary>
    public class HandleCampaign_Tests
    {
        private readonly ITestOutputHelper _output;

        public HandleCampaign_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "NetProofCampaign")]
        public void Handle_campaign_histories_are_permitted()
        {
            var mode = (Environment.GetEnvironmentVariable("LITEDB_PBT_MODE") ?? "Direct") == "Shared" ? ConnectionType.Shared : ConnectionType.Direct;
            var threads = Number("LITEDB_PBT_MAX_SUFFIX_THREADS", 3);
            var budget = TimeSpan.FromSeconds(Number("LITEDB_PBT_TIME_BUDGET_S", 20));
            var baseSeed = PropertyEnvironment.FirstSeed(1);
            var options = new PropertyOptions(mode)
            {
                MaxSuffixThreads = threads,
                AccessKindNames = PropertyEnvironment.AccessKinds() ?? new[] { OrdinaryAccess.KindName, LegacyAccess.KindName, HandleAccessKind.KindName },
            };
            HandleExecution.Statistics.Clear();
            var watch = Stopwatch.StartNew();
            var writer = new TimedWriter(watch);
            var log = writer.GetStringBuilder();
            var seed = baseSeed;
            PropertyRunResult failed = null;
            TimeSpan? firstFailureAt = null;
            var statistics = new CaseStatistics();
            var cases = 0;

            while (failed == null && watch.Elapsed < budget)
            {
                var run = ParallelPropertyRunner.Run(seed, 10, options, writer, parallel: true);
                if (run.Verdict == PropertyVerdict.NotApplicable) throw new InvalidOperationException(run.ToString());
                cases += run.CasesRun;
                seed += run.CasesRun;
                if (run.Verdict == PropertyVerdict.Failed)
                {
                    failed = run;
                    firstFailureAt = writer.FirstFailure;
                }
            }

            // Rate phase: further seeds, no investigation.
            var failures = new List<string>();
            var identities = new Dictionary<string, int>();
            var rateCases = 0;
            while (failed != null && watch.Elapsed < budget)
            {
                var propertyCase = PropertyCaseGenerator.Parallel(seed, options);
                var failure = CaseEvaluator.Evaluate(propertyCase, options, statistics);
                rateCases++;
                if (failure != null)
                {
                    failures.Add(seed + ":" + failure.Identity);
                    identities[failure.Identity] = identities.TryGetValue(failure.Identity, out var n) ? n + 1 : 1;
                }
                seed++;
            }

            var summary = new StringBuilder();
            summary.AppendLine($"campaign mode={mode} maxSuffixThreads={threads} baseSeed={baseSeed} budget={budget.TotalSeconds:0}s " +
                $"wall={watch.Elapsed.TotalSeconds:0.0}s casesUntilFirstFailureOrBudget={cases} kinds={string.Join(",", options.AccessKindNames)}");
            summary.AppendLine("environment: " + PropertyEnvironment.Describe(options) +
                $" earlyTimeoutMs={HandleAccessKind.EarlyTimeoutMilliseconds} selfWaitFailFast={HandleAccessKind.SelfWaitFailFast} denseHandoff={HandleAccessKind.DenseHandoff} callbacks={HandleAccessKind.Callbacks} executingSelfWaitFailFast={HandleAccessKind.ExecutingSelfWaitFailFast} lentCallbacks={HandleAccessKind.LentCallbacks}");
            summary.AppendLine("handle command results: " + HandleExecution.DescribeStatistics());
            summary.AppendLine("statistics (rate phase): " + statistics);
            if (failed == null)
            {
                summary.AppendLine($"verdict: PASSED ({cases} cases, seeds {baseSeed}..{seed - 1})");
            }
            else
            {
                summary.AppendLine($"verdict: FAILED first failing seed {failed.Failure.CaseSeed} [{failed.Failure.Original.Identity}] " +
                    $"at ~{firstFailureAt?.TotalSeconds:0.0}s; classification {failed.Failure.Classification}; " +
                    $"replays {failed.Failure.ReplayFailures}/{failed.Failure.ReplayRuns}; shrunk reproductions {failed.Failure.ShrunkReproductions}/{failed.Failure.ReplayRuns}");
                summary.AppendLine($"rate phase: {failures.Count}/{rateCases} further cases failed ({string.Join(", ", identities.Select(p => p.Key + "=" + p.Value))}); " +
                    $"failing seeds: {string.Join(" ", failures.Take(60))}");
                summary.AppendLine(failed.Failure.ToString());
            }
            summary.AppendLine("--- runner log ---");
            summary.Append(log);
            Write(summary.ToString(), mode, threads, baseSeed);

            Assert.True(failed == null, summary.ToString());
        }

        /// <summary>Notes when the runner reports the first failing case (before it replays and shrinks it).</summary>
        private sealed class TimedWriter : StringWriter
        {
            private readonly Stopwatch _watch;

            public TimedWriter(Stopwatch watch)
            {
                _watch = watch;
            }

            public TimeSpan? FirstFailure { get; private set; }

            public override void WriteLine(string value)
            {
                if (this.FirstFailure == null && value != null && value.StartsWith("case seed ", StringComparison.Ordinal))
                    this.FirstFailure = _watch.Elapsed;
                base.WriteLine(value);
            }
        }

        private void Write(string text, ConnectionType mode, int threads, int baseSeed)
        {
            foreach (var line in text.Split('\n').Take(400)) _output.WriteLine(line.TrimEnd('\r'));
            var directory = Environment.GetEnvironmentVariable("LITEDB_PBT_ARTIFACT_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"campaign-{mode}-t{threads}-seed{baseSeed}.txt"), text);
        }

        private static int Number(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }
}
