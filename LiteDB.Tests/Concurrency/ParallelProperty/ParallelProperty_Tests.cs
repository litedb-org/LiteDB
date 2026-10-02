using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// State-machine property tests of the public collection and transaction API: generated command
    /// sequences (auto-commit calls and per-thread BeginTrans/Commit/Rollback blocks) must agree with
    /// a model of collection contents, snapshots and locks, sequentially and, on real threads, as a
    /// history the model permits (see <see cref="PermittedHistoryChecker"/>). Raise the case count
    /// with LITEDB_PBT_ITERATIONS; regenerate one case's inputs with LITEDB_PBT_SEED.
    /// </summary>
    public class ParallelProperty_Tests
    {
        private const int SequentialCases = 20;
        private const int ParallelCases = 30;

        private readonly ITestOutputHelper _output;

        public ParallelProperty_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Sequential_commands_agree_with_the_model_after_every_step(ConnectionType mode)
        {
            var result = ParallelPropertyRunner.Run(PropertyEnvironment.FirstSeed(1), PropertyEnvironment.Count(SequentialCases),
                new PropertyOptions(mode), this.Log(), parallel: false);

            AssertPassed(result);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Concurrent_histories_are_permitted_by_the_model(ConnectionType mode)
        {
            var result = ParallelPropertyRunner.Run(PropertyEnvironment.FirstSeed(1), PropertyEnvironment.Count(ParallelCases),
                new PropertyOptions(mode), this.Log(), parallel: true);

            AssertPassed(result);
            Assert.True(result.Statistics.OverlappingOperations > 0, "no operations of different threads overlapped: " + result);
        }

        private static void AssertPassed(PropertyRunResult result)
        {
            // NOT APPLICABLE (an access kind missing from this build) is reported as such and never passes.
            Assert.True(result.Verdict != PropertyVerdict.NotApplicable, result.ToString());
            Assert.True(result.Passed, result.ToString());
        }

        private TextWriter Log() => new OutputWriter(_output);

        private sealed class OutputWriter : StringWriter
        {
            private readonly ITestOutputHelper _output;

            public OutputWriter(ITestOutputHelper output)
            {
                _output = output;
            }

            public override void WriteLine(string value) => _output.WriteLine(value ?? "");
        }
    }
}
