using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Controls for the property tests themselves: deliberately broken engines must be caught (with a
    /// shrunk counterexample), a real lock timeout produced by crossed transactions must be accepted,
    /// and a run that needs a library surface this build lacks must say NOT APPLICABLE.
    /// </summary>
    public class ParallelPropertyDetection_Tests
    {
        private readonly ITestOutputHelper _output;

        public ParallelPropertyDetection_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void An_engine_that_loses_committed_writes_fails_the_sequential_property_with_a_shrunk_counterexample(ConnectionType mode)
        {
            var options = Options(mode, e => new LosesCommittedWritesEngine(e));

            var result = ParallelPropertyRunner.Run(1, 20, options, null, parallel: false);

            _output.WriteLine(result.ToString());
            Assert.Equal(PropertyVerdict.Failed, result.Verdict);
            Assert.StartsWith("sequential:", result.Failure.Original.Identity);
            Assert.True(result.Failure.ShrunkCase.CommandCount < result.Failure.OriginalCase.CommandCount, result.ToString());
            Assert.Equal("reproducible", result.Failure.Classification);
        }

        [Fact]
        public void An_engine_that_loses_committed_writes_fails_the_parallel_property()
        {
            var result = ParallelPropertyRunner.Run(1, 40, Options(ConnectionType.Direct, e => new LosesCommittedWritesEngine(e)), null, parallel: true);

            _output.WriteLine(result.ToString());
            Assert.Equal(PropertyVerdict.Failed, result.Verdict);
            Assert.Equal("not-permitted", result.Failure.Original.Identity);
            Assert.NotNull(result.Failure.Original.Records);
        }

        [Fact]
        public void An_engine_that_exposes_a_half_done_update_passes_alone_but_fails_concurrently()
        {
            var options = Options(ConnectionType.Direct, e => new NonAtomicUpdateEngine(e));

            var sequential = ParallelPropertyRunner.Run(1, 20, options, null, parallel: false);
            var parallel = ParallelPropertyRunner.Run(1, 150, options, null, parallel: true);
            // Exposure needs another thread's read inside the update's window, a native-thread race (evidence
            // class 2): 1 of 33 local campaigns of 150 cases missed it (net10.0, load ~22). A second campaign from
            // another seed keeps the assertion (the property must catch this engine) and makes a miss negligible.
            if (parallel.Passed)
            {
                _output.WriteLine("first campaign missed the race: " + parallel);
                parallel = ParallelPropertyRunner.Run(2, 150, options, null, parallel: true);
            }

            _output.WriteLine(sequential.ToString());
            _output.WriteLine(parallel.ToString());
            Assert.True(sequential.Passed, sequential.ToString());
            Assert.Equal(PropertyVerdict.Failed, parallel.Verdict);
            Assert.Equal("not-permitted", parallel.Failure.Original.Identity);
            Assert.Contains(parallel.Failure.ShrunkCase.Suffixes.SelectMany(s => s).SelectMany(u => u.Commands), c => c.Op == DataOperations.Update);
        }

        [Fact]
        public void A_real_lock_timeout_between_crossed_transactions_is_a_permitted_history()
        {
            var options = new PropertyOptions(ConnectionType.Direct);
            var recorder = new HistoryRecorder();
            string[] final;
            using (var database = new PropertyDatabase(options))
            {
                var shared = new ConcurrentDictionary<int, object>();
                var firstLocksHeld = new CountdownEvent(2);
                var threads = new[] { 1, 2 }.Select(t => new Thread(() =>
                {
                    // T1 locks c0 then wants c1; T2 locks c1 then wants c0: one of them must time out.
                    var context = new ThreadContext(t, database.Database, shared);
                    recorder.Execute(Legacy(LegacyAccess.BeginTrans), context);
                    recorder.Execute(Legacy(DataOperations.Insert, t - 1, t, 10 + t), context);
                    firstLocksHeld.Signal();
                    firstLocksHeld.Wait();
                    recorder.Execute(Legacy(DataOperations.Insert, 2 - t, t, 20 + t), context);
                    recorder.Execute(Legacy(LegacyAccess.Commit), context);
                }) { IsBackground = true }).ToList();
                threads.ForEach(t => t.Start());
                Assert.True(threads.All(t => t.Join(TimeSpan.FromSeconds(30))), "crossed transactions did not finish: " + string.Join(", ", recorder.Running()));
                final = database.ProbeAndRead(2);
            }

            var records = recorder.Snapshot();
            _output.WriteLine(string.Join(Environment.NewLine, records.OrderBy(r => r.Start)) + Environment.NewLine + string.Join(" ", final));
            Assert.Contains(records, r => r.Result.Kind == OutcomeKind.Timeout);
            var check = PermittedHistoryChecker.Check(new ModelState(ConnectionType.Direct, 2, 5, 3), records, final);
            Assert.True(check.Permitted, check.Diagnosis);

            // The same history with the timed-out write reported as successful is not permitted.
            var tampered = records.Select(r => r.Result.Kind != OutcomeKind.Timeout ? r
                : new OperationRecord(r.Thread, r.Index, r.Command, Observation.Ok(r.Command.Key), r.Start, r.End)).ToList();
            Assert.False(PermittedHistoryChecker.Check(new ModelState(ConnectionType.Direct, 2, 5, 3), tampered, final).Permitted);
        }

        [Fact]
        public void A_run_requesting_an_access_kind_this_build_lacks_is_not_applicable()
        {
            var options = new PropertyOptions(ConnectionType.Direct) { AccessKindNames = new[] { "ordinary", "handle" } };

            var result = ParallelPropertyRunner.Run(1, 5, options, null, parallel: true);

            Assert.Equal(PropertyVerdict.NotApplicable, result.Verdict);
            Assert.False(result.Passed);
            Assert.Equal(0, result.CasesRun);
            Assert.Contains("handle", result.NotApplicableReason);
        }

        // Small shrink and replay budgets keep each self-test well inside the per-test time limit.
        private static PropertyOptions Options(ConnectionType mode, Func<LiteDB.Engine.ILiteEngine, LiteDB.Engine.ILiteEngine> decorator) =>
            new PropertyOptions(mode)
            {
                EngineDecorator = decorator,
                AccessKindNames = null,
                ShrinkAttempts = 4,
                ShrinkBudget = 60,
                ReproductionRuns = 5,
                ArtifactDirectory = null,
            };

        private static PropertyCommand Legacy(string op, int collection = 0, int key = 0, int payload = 0) =>
            new PropertyCommand(LegacyAccess.KindName, op, collection, key, payload);
    }
}
