#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class BackgroundCheckpointSlowSync_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
                foreach (var data in new[] { false, true })
                    foreach (var sync in new[] { 1, 2 })
                        foreach (var close in new[] { false, true })
                            yield return new object[] { password, data, sync, close };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task HighWaterWaiterCannotBypassSlowNativeSyncAndCloseJoinsIt(
            string password, bool data, int sync, bool close)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            var state = BackgroundCheckpointBoundaryMatrix_Tests.State(scenario.Engine);
            using var syncing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            var hits = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                var target = data ? NativeFileSyncCollection.CanonicalPath(scenario.File.Filename)
                    : NativeFileSyncCollection.LogPath(scenario.File.Filename);
                if (worker.IsWorkerThread && path == target && Interlocked.Increment(ref hits) == sync)
                {
                    syncing.Set();
                    AutomaticCheckpointWorker_Tests.Wait(release);
                }
                return 0;
            };
            Task<Exception> writer = null, closing = null;
            try
            {
                using (worker.Pause())
                {
                    for (var i = 1; i <= 8; i++) scenario.Database.UserVersion = i;
                    scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                }
                AutomaticCheckpointWorker_Tests.Wait(syncing);
                scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = waiting.Set;
                writer = DirectCheckpointLifetime_Tests.Start(() =>
                {
                    scenario.Database.BeginTrans().Should().BeTrue();
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
                    scenario.Database.Rollback().Should().BeTrue();
                    scenario.Database.UserVersion = 9;
                });
                AutomaticCheckpointWorker_Tests.Wait(waiting);
                writer.IsCompleted.Should().BeFalse();
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                if (close)
                {
                    closing = DirectCheckpointLifetime_Tests.Start(scenario.Engine.Dispose);
                    SpinWait.SpinUntil(() => state.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    await DirectCheckpointLifetime_Tests.Completes(writer);
                    writer.Result.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.ENGINE_DISPOSED);
                    closing.IsCompleted.Should().BeFalse();
                    state.Disposed.Should().BeFalse("native sync still owns a live file handle");
                }
            }
            finally
            {
                release.Set();
                if (writer != null) await DirectCheckpointLifetime_Tests.Completes(writer);
                if (closing != null) await DirectCheckpointLifetime_Tests.Completes(closing);
                NativeFileSync.SimulateErrno = null;
            }
            if (close)
            {
                closing.Result.Should().BeNull();
                worker.HasExited.Should().BeTrue();
            }
            else
            {
                writer.Result.Should().BeNull();
                scenario.Database.Checkpoint();
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            }
            hits.Should().BeGreaterThanOrEqualTo(sync);
            scenario.VerifyRecovery(close ? 8 : 9);
        }
    }
}
#endif
