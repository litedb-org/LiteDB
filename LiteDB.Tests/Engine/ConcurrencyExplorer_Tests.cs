using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.ConcurrencyTesting;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The general concurrency explorer on the upstream adapter (ordinary and legacy access, Direct
    /// and Shared). The default set is representative (every dimension value, both modes, every
    /// scenario); LITEDB_EXPLORER_FULL=1 runs the whole matrix of every scenario; LITEDB_EXPLORER_VECTOR
    /// replays one recorded vector; lifetime-chaos programs run from (seed, step) and replay with
    /// LITEDB_LIFETIME_CHAOS=seed:step (evidence class 2). Registered known findings (<see cref="ExplorerKnownFindings"/>)
    /// are reported, not failed; every other failure fails with its failure id and retained artifact.
    /// See docs/concurrency-explorer.md.
    /// </summary>
    [Collection(Issues.NativeFileSyncCollection.Name)]
    public class ConcurrencyExplorer_Tests
    {
        private readonly ITestOutputHelper _output;

        public ConcurrencyExplorer_Tests(ITestOutputHelper output) => _output = output;

        public static IEnumerable<object[]> Representative() => ExplorerSelection.Representative().Select(vector => new object[] { vector.ToString() });

        [Theory]
        [MemberData(nameof(Representative))]
        public void Forced_schedules_keep_progress_durability_and_ownership(string vector) => this.Run(vector);

        [Fact]
        public void Full_matrix_when_requested()
        {
            if (Environment.GetEnvironmentVariable("LITEDB_EXPLORER_FULL") != "1")
            {
                _output.WriteLine("LITEDB_EXPLORER_FULL is not 1: the representative theory covers the default set.");
                return;
            }
            // Optional narrowing for campaigns: a regex over the vector text and a sampling stride.
            var filter = Environment.GetEnvironmentVariable("LITEDB_EXPLORER_FILTER");
            var sample = int.TryParse(Environment.GetEnvironmentVariable("LITEDB_EXPLORER_SAMPLE"), out var every) && every > 1 ? every : 1;
            var report = Environment.GetEnvironmentVariable("LITEDB_EXPLORER_REPORT");
            var failures = new List<string>();
            var counts = new Dictionary<string, int>();
            var index = 0;
            foreach (var vector in ExplorerScenarios.Matrix(ExplorerAccessKinds.Upstream))
            {
                var text = vector.ToString();
                if (filter != null && !System.Text.RegularExpressions.Regex.IsMatch(text, filter)) continue;
                if (index++ % sample != 0) continue;
                var result = this.Execute(text, out var known);
                var key = result.Verdict + (known != null ? " known " + known.Id : result.Verdict == ExplorerVerdict.Failed ? " " + result.Fingerprint : "");
                counts[key] = (counts.TryGetValue(key, out var count) ? count : 0) + 1;
                if (report != null) File.AppendAllText(report, result.Verdict + "\t" + result.Fingerprint + "\t" + text + "\t" + result.ArtifactPath + Environment.NewLine);
                if (result.Verdict == ExplorerVerdict.Failed && known == null) failures.Add(result.ToString());
            }
            foreach (var pair in counts.OrderBy(pair => pair.Key)) _output.WriteLine(pair.Value + "\t" + pair.Key);
            Assert.True(failures.Count == 0, failures.Count + " failure(s):" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(20)));
        }

        [Fact]
        public void ConcurrencyExplorerReplay()
        {
            var vector = Environment.GetEnvironmentVariable("LITEDB_EXPLORER_VECTOR") ?? ExplorerSelection.Representative().First().ToString();
            try { this.Run(vector); }
            finally
            {
                // A finalizer failure (a leaked page buffer's ENSURE) is attributed to the replayed vector, not a later test.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>A fixed set of lifetime-chaos programs (seed 2947, steps 1-16); LITEDB_LIFETIME_CHAOS=seed:step replays one.</summary>
        public static IEnumerable<object[]> ChaosSteps() => Enumerable.Range(1, 16).Select(step => new object[] { 2947, step });

        [Theory]
        [MemberData(nameof(ChaosSteps))]
        public void Lifetime_chaos_programs_keep_progress_durability_and_ownership(int seed, int step) => this.RunChaos(seed, step);

        [Fact]
        public void LifetimeChaosReplay()
        {
            var text = Environment.GetEnvironmentVariable("LITEDB_LIFETIME_CHAOS") ?? "2947:1";
            var parts = text.Split(':');
            try { this.RunChaos(int.Parse(parts[0]), int.Parse(parts[1]), history: true); }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void RunChaos(int seed, int step, bool history = false)
        {
            var program = LifetimeChaosProgram.Generate(seed, step, ExplorerAccessKinds.All.Select(access => access.Name).ToArray(),
                ExplorerKnownFindings.IncludeKnown);
            _output.WriteLine(program.Program);
            var directory = Path.Combine(Path.GetTempPath(), "litedb-chaos-" + Guid.NewGuid().ToString("N"));
            ExplorerResult result;
            using (var host = new ExplorerLocalHost())
                result = ExplorerRun.Execute(program.Vector, directory, host, program);
            _output.WriteLine(result.ToString());
            if (history && result.HistoryPath != null && File.Exists(result.HistoryPath)) _output.WriteLine(File.ReadAllText(result.HistoryPath));
            var known = ExplorerKnownFindings.Match(result);
            if (known != null)
            {
                _output.WriteLine("KNOWN FINDING " + known.Id + " reproduced: " + result);
                return;
            }
            if (result.Verdict != ExplorerVerdict.Failed && Directory.Exists(directory)) Directory.Delete(directory, true);
            Assert.True(result.Verdict == ExplorerVerdict.Passed, result + Environment.NewLine + result.Failure);
        }

        [Fact]
        public void The_handle_access_kind_is_not_applicable_on_upstream_and_never_passes()
        {
            var vector = new ExplorerVector { Scenario = "callback-pause", Configuration = new ExplorerConfiguration { Access = "handle" } };
            var result = this.Execute(vector.ToString(), out _);
            Assert.Equal(ExplorerVerdict.NotApplicable, result.Verdict);
            Assert.False(result.Passed);
            Assert.Contains("handle-api", result.NotApplicableReason);
        }

        private void Run(string text)
        {
            var result = this.Execute(text, out var known);
            if (result.Verdict == ExplorerVerdict.Failed && known != null)
            {
                _output.WriteLine("KNOWN FINDING " + known.Id + " reproduced: " + result);
                return;
            }
            Assert.True(result.Verdict != ExplorerVerdict.Failed, result + Environment.NewLine + result.Failure);
        }

        private ExplorerResult Execute(string text, out ExplorerKnownFinding known)
        {
            var vector = ExplorerVector.Parse(text);
            known = null;
            var excluded = ExplorerKnownFindings.Excluding(vector);
            if (excluded != null)
            {
                _output.WriteLine("EXCLUDED by known finding " + excluded.Id + ": " + vector);
                return new ExplorerResult { Verdict = ExplorerVerdict.NotApplicable, Vector = vector, NotApplicableReason = "known finding " + excluded.Id };
            }
            var directory = Path.Combine(Path.GetTempPath(), "litedb-explorer-" + Guid.NewGuid().ToString("N"));
            ExplorerResult result;
            using (var host = new ExplorerLocalHost())
                result = ExplorerRun.Execute(vector, directory, host);
            _output.WriteLine(result.ToString());
            known = ExplorerKnownFindings.Match(result);
            // Failures retain the fixture, history and explorer-failure.json; passing runs leave nothing.
            if (result.Verdict != ExplorerVerdict.Failed && Directory.Exists(directory)) Directory.Delete(directory, true);
            return result;
        }
    }
}
