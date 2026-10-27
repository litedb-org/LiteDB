#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Internals.DirectHostGroupCommit_Tests;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitCheckpointOrdering_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Checkpoint_waits_for_captured_group_sync_and_observes_its_failure(bool fail)
        {
            using var scenario = new BackgroundCheckpointScenario();
            var log = NativeFileSyncCollection.LogPath(scenario.File.Filename);
            using var syncing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var checkpointWaiting = new ManualResetEventSlim();
            using var followerReady = new ManualResetEventSlim();
            var syncs = 0;
            var indexEntries = 0;
            var dataWrites = 0;
            var acknowledged = false;
            var followerAcknowledged = false;
            Task<Exception> writer = null, checkpoint = null, follower = null;
            scenario.Engine.SimulateDataWriteFail = _ => Interlocked.Increment(ref dataWrites);
            scenario.Engine.CheckpointStage = stage =>
            {
                if (stage == "before-barrier-lock") checkpointWaiting.Set();
                if (stage == "before-index-lock") Interlocked.Increment(ref indexEntries);
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != log || Interlocked.Increment(ref syncs) != 1) return 0;
                syncing.Set();
                Wait(release);
                return fail ? 5 : 0;
            };
            try
            {
                writer = Start(() =>
                {
                    scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
                    acknowledged = true;
                });
                Wait(syncing);
                follower = Start(() =>
                {
                    scenario.Database.BeginTrans().Should().BeTrue();
                    scenario.Database.GetCollection("rows").Delete(99).Should().BeFalse();
                    followerReady.Set();
                    scenario.Database.Commit().Should().BeTrue();
                    followerAcknowledged = true;
                });
                Wait(followerReady);
                checkpoint = Start(() => scenario.Engine.Checkpoint());
                Wait(checkpointWaiting);
                indexEntries.Should().Be(0, "checkpoint cannot inspect/reset a generation while its group sync owns the barrier");
                dataWrites.Should().Be(0);
                acknowledged.Should().BeFalse();
                release.Set();
                await Completes(Task.WhenAll(writer, checkpoint, follower));
                if (fail)
                {
                    writer.Result.Should().BeAssignableTo<IOException>();
                    checkpoint.Result.Should().BeAssignableTo<IOException>();
                    follower.Result.Should().BeAssignableTo<IOException>();
                    scenario.State.Stopped.Should().BeTrue();
                    indexEntries.Should().Be(0);
                    dataWrites.Should().Be(0);
                    syncs.Should().Be(1, "a queued checkpoint cannot sync after leader failure");
                }
                else
                {
                    writer.Result.Should().BeNull();
                    checkpoint.Result.Should().BeNull();
                    follower.Result.Should().BeNull();
                    indexEntries.Should().Be(1);
                    dataWrites.Should().BeGreaterThan(0);
                    // Its initial exclusion attempt raced the active committer:
                    // that pass may safely backfill without reclaiming. A fresh
                    // attempt after transaction release must reclaim completely.
                    scenario.Database.Checkpoint();
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
                }
            }
            finally
            {
                release.Set();
                try { await Finish(writer, checkpoint, follower); }
                finally
                {
                    NativeFileSync.SimulateErrno = null;
                    scenario.Engine.CheckpointStage = null;
                    scenario.Engine.SimulateDataWriteFail = null;
                }
            }
            acknowledged.Should().Be(!fail);
            followerAcknowledged.Should().Be(!fail);
            scenario.VerifyRecovery(acknowledged);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Pending_group_waits_for_checkpoint_and_preserves_terminal_failure(int failureStage)
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var peer = failureStage == 0 ? null : new LiteDatabase(scenario.File.Filename);
            using var captured = new ManualResetEventSlim();
            using var enterGroup = new ManualResetEventSlim();
            using var groupWaiting = new ManualResetEventSlim();
            using var checkpointEntered = new ManualResetEventSlim();
            using var releaseCheckpoint = new ManualResetEventSlim();
            var groupSyncs = 0;
            var writes = 0;
            var acknowledged = false;
            var pendingVersion = -1;
            Task<Exception> writer = null, checkpoint = null;
            scenario.State.AtCrashPoint = stage =>
            {
                if (stage == "wal-before-checkpoint-barrier")
                {
                    captured.Set();
                    Wait(enterGroup);
                    groupWaiting.Set();
                }
                if (stage == "wal-before-durable-flush") Interlocked.Increment(ref groupSyncs);
            };
            scenario.Engine.CheckpointStage = stage =>
            {
                if (stage != "before-index-lock") return;
                checkpointEntered.Set();
                Wait(releaseCheckpoint);
                if (failureStage == 1) throw new IOException("checkpoint before writer");
            };
            scenario.Engine.SimulateDataWriteFail = _ =>
            {
                Interlocked.Increment(ref writes);
                if (failureStage == 2) throw new IOException("checkpoint data write");
            };
            try
            {
                writer = Start(() =>
                {
                    scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
                    acknowledged = true;
                });
                Wait(captured);
                pendingVersion = scenario.Disk.PendingCommitVersions.Single();
                checkpoint = Start(() => scenario.Wal.Checkpoint());
                Wait(checkpointEntered);
                enterGroup.Set();
                Wait(groupWaiting);
                groupSyncs.Should().Be(0, "leader cannot capture/sync a handle while checkpoint owns the barrier");
                acknowledged.Should().BeFalse();
                releaseCheckpoint.Set();
                await Completes(Task.WhenAll(writer, checkpoint));
                if (failureStage == 0)
                {
                    writer.Result.Should().BeNull();
                    checkpoint.Result.Should().BeNull();
                    groupSyncs.Should().Be(1);
                    scenario.Wal.CurrentReadVersion.Should().Be(pendingVersion, "a pending group prevents generation reset");
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0);
                    scenario.Database.Checkpoint();
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
                }
                else
                {
                    checkpoint.Result.Should().BeAssignableTo<IOException>();
                    checkpoint.Result.Message.Should().Contain(failureStage == 1 ? "before writer" : "data write");
                    writer.Result.Should().BeAssignableTo<IOException>();
                    scenario.State.Stopped.Should().BeTrue();
                    groupSyncs.Should().Be(0, "checkpoint publishes stop before releasing the barrier");
                    writes.Should().Be(failureStage == 1 ? 0 : 1);
                    Action peerRead = () => peer.GetCollection("rows").FindById(0);
                    Action peerWrite = () => peer.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2));
                    Action attach = () => { using var refused = new LiteDatabase(scenario.File.Filename); };
                    peerRead.Should().Throw<IOException>();
                    peerWrite.Should().Throw<IOException>();
                    attach.Should().Throw<IOException>();
                }
            }
            finally
            {
                enterGroup.Set();
                releaseCheckpoint.Set();
                try { await Finish(writer, checkpoint); }
                finally
                {
                    scenario.State.AtCrashPoint = null;
                    scenario.Engine.CheckpointStage = null;
                    scenario.Engine.SimulateDataWriteFail = null;
                }
            }
            acknowledged.Should().Be(failureStage == 0);
            peer?.Dispose();
            scenario.VerifyRecovery(acknowledged);
        }

        [Fact]
        public async Task Header_callback_fallback_can_wait_for_a_group_without_reversing_the_lock_order()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var syncing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fallbackAppended = new ManualResetEventSlim();
            var log = NativeFileSyncCollection.LogPath(scenario.File.Filename);
            var syncs = 0;
            Task<Exception> writer = null, fallback = null;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path == log && Interlocked.Increment(ref syncs) == 1)
                {
                    syncing.Set();
                    Wait(release);
                }
                return 0;
            };
            try
            {
                writer = Start(() => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1)));
                Wait(syncing);
                scenario.State.AtCrashPoint = phase =>
                {
                    // Fallback waits for its barrier before index publication,
                    // while still holding the header. Observe its actual append.
                    if (phase == "wal-confirmation-after-write") fallbackAppended.Set();
                };
                // A pragma transaction has a commit callback and cannot use TryCommit.
                fallback = Start(() => scenario.Database.UserVersion = 23);
                Wait(fallbackAppended);
                release.Set();
                await Completes(Task.WhenAll(writer, fallback));
                writer.Result.Should().BeNull();
                fallback.Result.Should().BeNull();
                scenario.Database.UserVersion.Should().Be(23);
                scenario.Database.Checkpoint();
            }
            finally
            {
                release.Set();
                try { await Finish(writer, fallback); }
                finally
                {
                    scenario.State.AtCrashPoint = null;
                    NativeFileSync.SimulateErrno = null;
                }
            }
            scenario.VerifyRecovery(acknowledged: true, userVersion: 23);
        }

        private static Task Finish(params Task<Exception>[] tasks) =>
            Completes(Task.WhenAll(tasks.Where(task => task != null)));
    }
}
#endif
