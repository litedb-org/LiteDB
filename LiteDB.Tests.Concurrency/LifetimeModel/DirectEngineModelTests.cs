using LiteDB.Tests.Concurrency.LifetimeModel.Direct;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// The Direct engine's admission and maintenance paths (LockService, TransactionGate,
    /// TransactionMonitor, LiteEngine close/rebuild/fatal stop) under the interleavings Coyote
    /// explores, against the generic lifetime properties. See docs/concurrency-models.md.
    /// </summary>
    public class DirectEngineModelTests
    {
        private readonly ITestOutputHelper _output;

        public DirectEngineModelTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// One maintenance operation at a time: a user Dispose racing another close or a rebuild
        /// is the known finding pinned below, so it is explored separately.
        /// </summary>
        private static DirectScenario SingleMaintenance() => new DirectScenario { ConcurrentDispose = false };

        private ModelRunResult Run(string name, DirectMutation mutation, DirectScenario scenario, ExplorationStrategy strategy, uint seed = 1)
        {
            var options = new ModelRunOptions { Seed = seed, Strategy = strategy }.WithEnvironment();
            var result = ModelRunner.Run(name, () => new DirectEngineModel(mutation, scenario), options);
            _output.WriteLine(result.Summary);
            return result;
        }

        [Theory]
        [InlineData(ExplorationStrategy.Random)]
        [InlineData(ExplorationStrategy.Prioritization)]
        public void Direct_engine_admission_and_maintenance_keep_the_lifetime_properties(ExplorationStrategy strategy)
        {
            var result = this.Run("direct", DirectMutation.None, SingleMaintenance(), strategy);
            Assert.False(result.BugFound, result.Summary + "\n" + result.Bug);
        }

        [Theory]
        [InlineData(DirectMutation.UnfencedRegistration, "no fresh admission after close acquired")]
        [InlineData(DirectMutation.NestedLeaseNotExempt, "no owner rejected while its transaction is active")]
        [InlineData(DirectMutation.UnboundedGateWaits, "Liveness violated")]
        public void Broken_variant_is_caught(DirectMutation mutation, string property)
        {
            var result = this.Run($"direct-{mutation}", mutation, SingleMaintenance(), ExplorationStrategy.Prioritization);
            Assert.True(result.BugFound, $"{mutation} was not caught: {result.Summary}");
            Assert.Contains(property, result.Bug);
        }

        /// <summary>
        /// Known finding (dev 7b71bc4d): LiteEngine.Close returns at once when another close already
        /// set _state.Disposed (LiteEngine.cs:213). A Dispose racing Rebuild's internal Close therefore
        /// returns while Rebuild goes on to reopen the engine (Rebuild.cs:66), and fresh work is admitted
        /// after Dispose returned. Reproduced on real code (docs/concurrency-models.md, "Findings").
        /// When the code is fixed, update the model to the fix and turn this into a passing check.
        /// </summary>
        [Fact]
        public void Known_finding_dispose_during_rebuild_lets_fresh_work_in_after_dispose_returned()
        {
            var scenario = new DirectScenario { Workers = new[] { OpKind.Fresh }, Maintenance = new[] { OpKind.Rebuild }, ConcurrentDispose = true };
            var result = this.Run("direct-finding-dispose-during-rebuild", DirectMutation.None, scenario, ExplorationStrategy.Prioritization);
            Assert.True(result.BugFound, result.Summary);
            Assert.Contains("no fresh admission after close acquired", result.Bug);
            Assert.Contains("Rebuild.cs:66", result.Bug);
        }

        /// <summary>
        /// Known finding (dev 7b71bc4d): a second, concurrent Dispose returns while the first close is
        /// still running (LiteEngine.cs:213), so work that passed validation earlier can still be
        /// admitted after that Dispose returned. The early return is reproduced on real code; the
        /// admission window (LiteEngine.cs:215-220) has no hook and was not hit by a real-thread stress run.
        /// </summary>
        [Fact]
        public void Known_finding_concurrent_dispose_returns_before_the_close_completes()
        {
            var scenario = new DirectScenario { Maintenance = new[] { OpKind.Close }, ConcurrentDispose = true };
            var result = this.Run("direct-finding-concurrent-dispose", DirectMutation.None, scenario, ExplorationStrategy.Prioritization);
            Assert.True(result.BugFound, result.Summary);
            Assert.Contains("Dispose returned early (already disposed)", result.Bug);
        }

        [Fact]
        public void A_recorded_trace_replays_the_same_violation()
        {
            var first = this.Run("direct-replay", DirectMutation.UnboundedGateWaits, SingleMaintenance(), ExplorationStrategy.Random, seed: 3);
            Assert.True(first.BugFound, first.Summary);
            var replay = ModelRunner.Run("direct-replayed", () => new DirectEngineModel(DirectMutation.UnboundedGateWaits, SingleMaintenance()),
                new ModelRunOptions { Iterations = 1, ReplayTrace = System.IO.File.ReadAllText(first.TraceFile) });
            _output.WriteLine(replay.Summary);
            Assert.True(replay.BugFound, replay.Summary);
            Assert.Equal(first.Bug, replay.Bug);
        }
    }
}
