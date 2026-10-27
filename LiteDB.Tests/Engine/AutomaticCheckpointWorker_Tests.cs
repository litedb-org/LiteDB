using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AutomaticCheckpointWorker_Tests
    {
        internal static AutomaticCheckpointCoordinator Install(BackgroundCheckpointScenario scenario, int checkpoint = 1)
        {
            var worker = new AutomaticCheckpointCoordinator(scenario.Disk, scenario.Wal, scenario.State, checkpoint);
            scenario.Disk.AutomaticCheckpoint = worker;
            return worker;
        }

        internal static void Wait(ManualResetEventSlim signal) =>
            signal.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        [Fact]
        public void LazyWorkerCoalescesRequestsAndRetainsRequestRacingCompletion()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var first = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var second = new ManualResetEventSlim();
            using var worker = Install(scenario);
            var starts = 0;
            var passes = 0;
            var thread = 0;
            worker.BeforeCheckpointWorkerStart = () => starts++;
            worker.BeforeCheckpointWorkerPass = () =>
            {
                var current = Thread.CurrentThread.ManagedThreadId;
                if (Interlocked.Increment(ref passes) == 1)
                {
                    thread = current;
                    first.Set();
                    Wait(release);
                }
                else { current.Should().Be(thread); second.Set(); }
            };
            worker.State.Should().Be(AutomaticCheckpointState.Idle);
            starts.Should().Be(0);
            worker.Request(worker.Incarnation - 1);
            starts.Should().Be(0);
            worker.Request(worker.Incarnation);
            try
            {
                Wait(first);
                for (var i = 0; i < 100; i++) worker.Request(worker.Incarnation);
                worker.RequestEpoch.Should().Be(101);
            }
            finally { release.Set(); }
            Wait(second);
            using (worker.Pause()) passes.Should().Be(2);
            starts.Should().Be(1);
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
        }

        [Fact]
        public void OnlyLastPauseResumesAndCloseCancelsQueuedStarts()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var worker = Install(scenario);
            var passes = 0;
            worker.BeforeCheckpointWorkerPass = () => Interlocked.Increment(ref passes);
            var first = worker.Pause();
            var last = worker.Pause();
            worker.Request(worker.Incarnation);
            first.Dispose();
            first.Dispose();
            worker.State.Should().Be(AutomaticCheckpointState.Paused);
            passes.Should().Be(0);
            worker.Dispose();
            last.Dispose();
            worker.Request(worker.Incarnation);
            worker.ReadersChanged();
            worker.State.Should().Be(AutomaticCheckpointState.Stopped);
            passes.Should().Be(0);
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0);
        }

        [Fact]
        public void LastPauseReleaseStartsTheQueuedPass()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var entered = new ManualResetEventSlim();
            using var worker = Install(scenario);
            worker.BeforeCheckpointWorkerPass = entered.Set;
            var first = worker.Pause();
            var last = worker.Pause();
            worker.Request(worker.Incarnation);
            first.Dispose();
            entered.IsSet.Should().BeFalse();
            last.Dispose();
            Wait(entered);
            using (worker.Pause()) scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
        }

        [Fact]
        public async Task ExplicitExecutorCheckpointJoinsPassAndReturnsItsOwnCount()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var joining = new ManualResetEventSlim();
            using var worker = Install(scenario);
            worker.BeforeCheckpointWorkerPass = () => { entered.Set(); Wait(release); };
            worker.BeforeCheckpointWorkerPauseWait = joining.Set;
            worker.Request(worker.Incarnation);
            Task<int> checkpoint = null;
            try
            {
                Wait(entered);
                checkpoint = Task.Run(() => scenario.Wal.Checkpoint());
                Wait(joining);
                checkpoint.IsCompleted.Should().BeFalse();
            }
            finally { release.Set(); }
            (await checkpoint).Should().Be(0, "the worker completed the existing WAL before this caller's own attempt");
        }

        [Fact]
        public void StorageLockReentrancyIsRefusedBeforeJoining()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var worker = Install(scenario);
            lock (scenario.Disk.WalWriterLock)
            {
                Action checkpoint = () => scenario.Wal.Checkpoint();
                checkpoint.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("storage locks");
            }
        }

        [Fact]
        public void ReaderBackoffHasFixedDeadlineAndLastUnpinQueuesWithoutIo()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var reader = scenario.Engine.Query("rows", new Query());
            reader.Read().Should().BeTrue();
            scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
            using var pass = new ManualResetEventSlim();
            using var worker = Install(scenario);
            long now = 0;
            worker.Backoff.Timestamp = () => Interlocked.Read(ref now);
            worker.AfterCheckpointWorkerPass = pass.Set;
            worker.Request(worker.Incarnation);
            Wait(pass);
            using (worker.Pause())
            {
                worker.Backoff.RemainingMilliseconds.Should().Be(50);
                for (var i = 0; i < 100; i++) worker.Request(worker.Incarnation);
                worker.Backoff.RemainingMilliseconds.Should().Be(50);
                using (var younger = scenario.Engine.Query("rows", new Query())) younger.Read().Should().BeTrue();
                worker.Backoff.RemainingMilliseconds.Should().Be(50, "a younger unpin cannot unblock the oldest generation");
                var tail = scenario.Disk.GetFileLength(FileOrigin.Log);
                tail.Should().BeGreaterThan(0);
                // Unpin is state-only. Pause proves no worker can consume the request here.
                reader.Dispose();
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(tail);
                worker.Backoff.RemainingMilliseconds.Should().Be(0);
                pass.Reset();
            }
            Wait(pass);
            using (worker.Pause()) scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
            worker.Dispose();
            scenario.Disk.AutomaticCheckpoint = null;
            scenario.VerifyRecovery(true);
        }

        [Fact]
        public void DisableCancelsQueuedWorkAndReenableRetainsCurrentServices()
        {
            using var scenario = new BackgroundCheckpointScenario();
            scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
            using var entered = new ManualResetEventSlim();
            using var worker = Install(scenario);
            worker.BeforeCheckpointWorkerPass = entered.Set;
            using (worker.Pause())
            {
                worker.Request(worker.Incarnation);
                worker.Configure(0, scenario.Disk.GetFileLength(FileOrigin.Log));
            }
            worker.State.Should().Be(AutomaticCheckpointState.Idle);
            entered.IsSet.Should().BeFalse();
            using (worker.Pause())
            {
                worker.Configure(1, scenario.Disk.GetFileLength(FileOrigin.Log));
            }
            Wait(entered);
            using (worker.Pause()) scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
        }
        [Fact]
        public void DisableDuringRunningPassPreventsCompletionFromRescheduling()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var reader = scenario.Engine.Query("rows", new Query());
            reader.Read().Should().BeTrue();
            scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            using var worker = Install(scenario);
            var passes = 0;
            worker.BeforeCheckpointWorkerPass = () =>
            {
                Interlocked.Increment(ref passes);
                entered.Set();
                Wait(release);
            };
            worker.AfterCheckpointWorkerPass = completed.Set;
            worker.Request(worker.Incarnation);
            try
            {
                Wait(entered);
                worker.Configure(0, scenario.Disk.GetFileLength(FileOrigin.Log));
            }
            finally { release.Set(); }
            Wait(completed);
            using (worker.Pause())
            {
                passes.Should().Be(1);
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0);
            }
            worker.State.Should().Be(AutomaticCheckpointState.Idle);
        }

        [Fact]
        public void RepeatedNoProgressBacksOffToOneSecondWithoutLosingReaderData()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var reader = scenario.Engine.Query("rows", new Query());
            reader.Read().Should().BeTrue();
            scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
            using var completed = new ManualResetEventSlim();
            using var worker = Install(scenario);
            long now = 0;
            var passes = 0;
            worker.Backoff.Timestamp = () => Interlocked.Read(ref now);
            worker.BeforeCheckpointWorkerPass = () => Interlocked.Increment(ref passes);
            worker.AfterCheckpointWorkerPass = completed.Set;
            worker.Request(worker.Incarnation);
            var delays = new[] { 50, 100, 200, 400, 800, 1000, 1000 };
            for (var i = 0; i < delays.Length; i++)
            {
                Wait(completed);
                using (worker.Pause())
                {
                    passes.Should().Be(i + 1);
                    worker.Backoff.RemainingMilliseconds.Should().Be(delays[i]);
                    ((object)reader.Current.AsDocument).Should().BeEquivalentTo(BackgroundCheckpointScenario.Row(0));
                    completed.Reset();
                    if (i == delays.Length - 1) worker.Configure(0, scenario.Disk.GetFileLength(FileOrigin.Log));
                    else
                    {
                        Interlocked.Add(ref now, delays[i] * System.Diagnostics.Stopwatch.Frequency / 1000 + 1);
                        worker.PublishTail(scenario.Disk.GetFileLength(FileOrigin.Log));
                    }
                }
            }
            worker.Dispose();
            scenario.Disk.AutomaticCheckpoint = null;
            reader.Dispose();
            scenario.VerifyRecovery(true);
        }

    }
}
