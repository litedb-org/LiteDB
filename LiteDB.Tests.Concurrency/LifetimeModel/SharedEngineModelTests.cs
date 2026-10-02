using LiteDB.Tests.Concurrency.LifetimeModel.Shared;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// A Shared connection's admission and Dispose paths (SharedEngine calls, explicit
    /// transactions, readers streamed under the mutex, SharedMutexOwner and its holder thread)
    /// under the interleavings Coyote explores, against the generic lifetime properties.
    /// </summary>
    public class SharedEngineModelTests
    {
        private readonly ITestOutputHelper _output;

        public SharedEngineModelTests(ITestOutputHelper output) => _output = output;

        private ModelRunResult Run(string name, SharedMutation mutation, ExplorationStrategy strategy, SharedScenario scenario = null, uint seed = 1)
        {
            var options = new ModelRunOptions { Seed = seed, Strategy = strategy }.WithEnvironment();
            var result = ModelRunner.Run(name, () => new SharedEngineModel(mutation, scenario), options);
            _output.WriteLine(result.Summary);
            return result;
        }

        [Theory]
        [InlineData(ExplorationStrategy.Random)]
        [InlineData(ExplorationStrategy.Prioritization)]
        public void Shared_connection_admission_and_dispose_keep_the_lifetime_properties(ExplorationStrategy strategy)
        {
            var result = this.Run("shared", SharedMutation.None, strategy);
            Assert.False(result.BugFound, result.Summary + "\n" + result.Bug);
        }

        [Theory]
        [InlineData(SharedMutation.AdmissionIgnoresDispose, "no fresh admission after close acquired")]
        [InlineData(SharedMutation.UnboundedDisposeDrain, "WaitForAdmittedCalls")]
        public void Broken_variant_is_caught(SharedMutation mutation, string evidence)
        {
            var result = this.Run($"shared-{mutation}", mutation, ExplorationStrategy.Prioritization);
            Assert.True(result.BugFound, $"{mutation} was not caught: {result.Summary}");
            Assert.Contains(evidence, result.Bug);
        }

        /// <summary>
        /// docs/shared-mode-safety.md: "Waits that cross threads (a callback waiting for another
        /// thread's call) are not detected." Without a Dispose to end it, the model must report that
        /// hang: the callback waits for the other call, which waits for the gate the callback's call holds.
        /// </summary>
        [Fact]
        public void Callback_waiting_for_another_threads_call_hangs_as_documented()
        {
            var scenario = new SharedScenario
            {
                Workers = new[] { OpKind.CallbackDependency },
                Maintenance = new[] { OpKind.Rebuild, OpKind.Fatal },
                ConcurrentDispose = false,
                CallbackDependencyWithoutDispose = true,
            };
            var result = this.Run("shared-documented-callback-hang", SharedMutation.None, ExplorationStrategy.Random, scenario);
            Assert.True(result.BugFound, result.Summary);
            Assert.Contains("Liveness violated", result.Bug);
            Assert.Contains("SharedMutexOwner.cs:131", result.Bug);
            Assert.Contains("user callback: blocks until the other thread's call returns", result.Bug);
        }
    }
}
