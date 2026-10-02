using System;
using System.Diagnostics;
using System.IO;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    public enum PropertyVerdict
    {
        Passed,
        Failed,
        /// <summary>The run requested an access kind this build does not have. Not a pass, not a skip.</summary>
        NotApplicable
    }

    /// <summary>Outcome of a property run over consecutive case seeds.</summary>
    public sealed class PropertyRunResult
    {
        public PropertyVerdict Verdict { get; internal set; }
        public bool Passed => this.Verdict == PropertyVerdict.Passed;
        public string NotApplicableReason { get; internal set; }
        public bool Parallel { get; internal set; }
        public ConnectionType Mode { get; internal set; }
        public int FirstSeed { get; internal set; }
        public int CasesRun { get; internal set; }
        public int CommandsRun { get; internal set; }
        public CaseStatistics Statistics { get; } = new CaseStatistics();
        public TimeSpan Elapsed { get; internal set; }
        public string Environment { get; internal set; }
        public PropertyFailureReport Failure { get; internal set; }

        public override string ToString()
        {
            var what = $"{(this.Parallel ? "parallel" : "sequential")} property, {this.Mode}";
            if (this.Verdict == PropertyVerdict.NotApplicable) return $"{what}: NOT APPLICABLE: {this.NotApplicableReason}";
            return $"{what}: {this.CasesRun} cases from seed {this.FirstSeed}, {this.CommandsRun} commands, {this.Elapsed.TotalSeconds:0.0}s" +
                (this.Parallel ? " (" + this.Statistics + ")" : "") + ": " +
                (this.Passed ? "passed" : System.Environment.NewLine + this.Failure);
        }
    }

    /// <summary>
    /// Entry point for the state-machine property test, independent of any test framework (a fuzz
    /// target can wrap <see cref="Run(int, int, ConnectionType, TextWriter)"/>). Case <c>i</c> of a run uses
    /// seed <c>seed + i</c>. The seed regenerates a case's INPUTS exactly (prefix, suffixes; given the
    /// mode and options), which is replayable evidence; the thread schedule of a parallel case is the
    /// operating system's and is not replayed (native-thread evidence). Stops at the first failure,
    /// keeps its observed history, replays it, shrinks it and classifies how it reproduces.
    /// </summary>
    public static class ParallelPropertyRunner
    {
        /// <summary>The parallel property with default options.</summary>
        public static PropertyRunResult Run(int seed, int count, ConnectionType mode, TextWriter log) =>
            Run(seed, count, new PropertyOptions(mode), log, parallel: true);

        /// <summary>The sequential property with default options.</summary>
        public static PropertyRunResult RunSequential(int seed, int count, ConnectionType mode, TextWriter log) =>
            Run(seed, count, new PropertyOptions(mode), log, parallel: false);

        public static PropertyRunResult Run(int seed, int count, PropertyOptions options, TextWriter log, bool parallel)
        {
            var watch = Stopwatch.StartNew();
            var result = new PropertyRunResult
            {
                Parallel = parallel,
                Mode = options.Mode,
                FirstSeed = seed,
                Environment = PropertyEnvironment.Describe(options),
            };
            if (AccessKinds.Resolve(options.AccessKindNames, out var notApplicable) == null)
            {
                result.Verdict = PropertyVerdict.NotApplicable;
                result.NotApplicableReason = notApplicable;
                log?.WriteLine(result.ToString());
                return result;
            }

            for (var i = 0; i < count; i++)
            {
                var caseSeed = unchecked(seed + i);
                var propertyCase = parallel
                    ? PropertyCaseGenerator.Parallel(caseSeed, options)
                    : PropertyCaseGenerator.Sequential(caseSeed, options);
                var failure = CaseEvaluator.Evaluate(propertyCase, options, result.Statistics);
                result.CasesRun++;
                result.CommandsRun += propertyCase.CommandCount;
                if (failure != null)
                {
                    log?.WriteLine($"case seed {caseSeed} failed [{failure.Identity}]; replaying and shrinking");
                    result.Failure = Investigate(caseSeed, propertyCase, failure, options, log, result.Environment);
                    break;
                }
            }
            result.Verdict = result.Failure == null ? PropertyVerdict.Passed : PropertyVerdict.Failed;
            result.Elapsed = watch.Elapsed;
            log?.WriteLine(result.ToString());
            return result;
        }

        private static PropertyFailureReport Investigate(int caseSeed, PropertyCase propertyCase, CaseFailure failure,
            PropertyOptions options, TextWriter log, string environment)
        {
            var report = new PropertyFailureReport
            {
                CaseSeed = caseSeed,
                Environment = environment,
                Original = failure,
                OriginalCase = propertyCase,
                ReplayRuns = options.ReproductionRuns,
            };
            // Immediate replay of the unchanged inputs: a failure that does not recur is still a finding.
            report.ReplayFailures = CaseShrinker.CountReproductions(propertyCase, failure.Identity, options, options.ReproductionRuns);
            report.ShrunkCase = propertyCase;
            report.ShrunkFailure = failure;
            if (report.ReplayFailures > 0)
            {
                report.ShrunkCase = CaseShrinker.Shrink(propertyCase, failure, options, log, out var shrunkFailure, out var shrinkRuns);
                report.ShrunkFailure = shrunkFailure;
                report.ShrinkRuns = shrinkRuns;
                report.ShrunkReproductions = CaseShrinker.CountReproductions(report.ShrunkCase, failure.Identity, options, options.ReproductionRuns);
            }
            report.WriteArtifacts(options.ArtifactDirectory, log);
            return report;
        }
    }
}
