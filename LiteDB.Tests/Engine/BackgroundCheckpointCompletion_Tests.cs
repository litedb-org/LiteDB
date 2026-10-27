#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class BackgroundCheckpointCompletion_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FinalCloseJoinsSnapshotCompletionAfterWalWriterExit(bool callbackFallback)
        {
            using var scenario = new DirectCheckpointScenario(0);
            using var snapshots = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var cleaning = new ManualResetEventSlim();
            var hits = 0;
            scenario.Engine.SimulateCrashPoint = phase =>
            {
                if (phase == "transaction-before-completion-cleanup" && scenario.State.Closing) cleaning.Set();
                if (phase != "transaction-after-snapshot-disposal" || Interlocked.Increment(ref hits) != 1) return;
                var transaction = scenario.Engine.GetMonitor().GetThreadTransaction();
                if (callbackFallback) transaction.Pages.HasCommitCallbacks.Should().BeTrue();
                else transaction.Pages.CommitTicket.Should().BeGreaterThan(0, "this schedule must reach the grouped file-backed path");
                snapshots.Set();
                AutomaticCheckpointWorker_Tests.Wait(release);
            };
            var writer = DirectCheckpointLifetime_Tests.Start(() =>
            {
                scenario.Database.BeginTrans();
                scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2));
                if (callbackFallback) scenario.Engine.GetMonitor().GetThreadTransaction().Pages.Commit += _ => { };
                scenario.Database.Commit();
            });
            Task<Exception> closing = null;
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(snapshots);
                closing = DirectCheckpointLifetime_Tests.Start(scenario.Engine.Dispose);
                AutomaticCheckpointWorker_Tests.Wait(cleaning);
                closing.IsCompleted.Should().BeFalse("WAL exclusion does not cover snapshot teardown after the batch");
                scenario.State.Disposed.Should().BeFalse();
                scenario.Engine.GetMonitor().Transactions.Should().BeEmpty("close has claimed registry cleanup and must join completion");
            }
            finally
            {
                release.Set();
                await DirectCheckpointLifetime_Tests.Completes(writer);
                if (closing != null) await DirectCheckpointLifetime_Tests.Completes(closing);
            }
            writer.Result.Should().NotBeNull("close won before the acknowledgement boundary");
            closing.Result.Should().BeNull();
            hits.Should().BeGreaterThan(0);
            scenario.VerifyRecovery(uncertain: true);
            AllocationReachability.Verify(scenario.File.Filename, null);
        }
    }
}
#endif
