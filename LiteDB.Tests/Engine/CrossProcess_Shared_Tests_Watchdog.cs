using System;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

public class CrossProcess_Shared_Tests_Watchdog
{
    [Theory]
    [InlineData("queued", 0)]
    [InlineData("inserting", 0)]
    [InlineData("disposing connection", 20)]
    public async Task NoCompletedProgress_ExpiresIncludingSchedulingAndDisposal(string stage, int completed)
    {
        long now = 0;
        var progress = new SharedWorkerDiagnostics.Worker(() => now);
        progress.Progress(stage, completed);
        var worker = new TaskCompletionSource<bool>();
        var result = await SharedWorkerWatchdog.WaitAsync(worker.Task, () => progress.CompletedIdleMilliseconds,
            30000, milliseconds => { now += milliseconds; return Task.CompletedTask; });
        result.Should().BeFalse();
        now.Should().Be(30000);
    }

    [Fact]
    public async Task OnlyThisWorkersIncreasingCompletedCount_ExtendsDeadline()
    {
        long now = 0;
        var stalled = new SharedWorkerDiagnostics.Worker(() => now);
        var peer = new SharedWorkerDiagnostics.Worker(() => now);
        stalled.Progress("insert completed", 7);
        var worker = new TaskCompletionSource<bool>();
        var tick = 0;
        var result = await SharedWorkerWatchdog.WaitAsync(worker.Task, () => stalled.CompletedIdleMilliseconds,
            30000, milliseconds =>
            {
                now += 10000;
                peer.Progress("insert completed", ++tick);
                stalled.Progress("inserting", 7); // Neither repeat nor heartbeat is progress.
                stalled.Progress("fault label", 6); // A regressing counter cannot buy time either.
                return Task.CompletedTask;
            });
        result.Should().BeFalse();
        now.Should().Be(30000);
        peer.CompletedIdleMilliseconds.Should().Be(0);
        stalled.Snapshot().Should().Contain("completed=7;");
        stalled.Snapshot().Should().Contain("idleMs=0;").And.Contain("completedIdleMs=30000");
    }

    [Fact]
    public async Task SlowSuccessfulWorker_CanExceedAggregateDeadlineWhileCompletingInserts()
    {
        long now = 0;
        var progress = new SharedWorkerDiagnostics.Worker(() => now);
        var worker = new TaskCompletionSource<bool>();
        var completed = 0;
        var result = await SharedWorkerWatchdog.WaitAsync(worker.Task, () => progress.CompletedIdleMilliseconds,
            30000, milliseconds =>
            {
                now += 20000;
                progress.Progress("insert completed", ++completed);
                if (completed == 4) worker.SetResult(true);
                return Task.CompletedTask;
            });
        result.Should().BeTrue();
        completed.Should().Be(4);
        now.Should().Be(80000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressOrCompletionAtTimerWakeup_IsRechecked(bool complete)
    {
        long now = 0;
        var progress = new SharedWorkerDiagnostics.Worker(() => now);
        var worker = new TaskCompletionSource<bool>();
        var ticks = 0;
        var result = await SharedWorkerWatchdog.WaitAsync(worker.Task, () => progress.CompletedIdleMilliseconds,
            30000, milliseconds =>
            {
                now += milliseconds;
                progress.Progress("insert completed", ++ticks);
                if (complete || ticks == 2) worker.SetResult(true);
                return Task.CompletedTask;
            });
        result.Should().BeTrue();
        ticks.Should().Be(complete ? 1 : 2);
    }

    [Fact]
    public async Task TimerWakeup_UsesRemainingBudgetSinceLastCompletedInsert()
    {
        long now = 0;
        var progress = new SharedWorkerDiagnostics.Worker(() => now);
        var worker = new TaskCompletionSource<bool>();
        var ticks = 0;
        var result = await SharedWorkerWatchdog.WaitAsync(worker.Task, () => progress.CompletedIdleMilliseconds,
            30000, milliseconds =>
            {
                if (++ticks == 1)
                {
                    milliseconds.Should().Be(30000);
                    now = 1000;
                    progress.Progress("insert completed", 1);
                    now = 30000;
                }
                else
                {
                    milliseconds.Should().Be(1000);
                    now += milliseconds;
                }
                return Task.CompletedTask;
            });
        result.Should().BeFalse();
        ticks.Should().Be(2);
        now.Should().Be(31000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskFinishingDuringExpiredIdleRead_IsObserved(bool fault)
    {
        var worker = new TaskCompletionSource<bool>();
        var failure = new InvalidOperationException("completed during idle read");
        var waiting = SharedWorkerWatchdog.WaitAsync(worker.Task, () =>
        {
            if (fault) worker.SetException(failure);
            else worker.SetResult(true);
            return 30000;
        }, 30000, milliseconds => throw new InvalidOperationException("must not delay"));
        if (fault) (await Assert.ThrowsAsync<InvalidOperationException>(() => waiting)).Should().BeSameAs(failure);
        else (await waiting).Should().BeTrue();
    }

    [Fact]
    public async Task FaultWhileWaiting_PropagatesOriginalException()
    {
        var worker = new TaskCompletionSource<bool>();
        var failure = new InvalidOperationException("worker failed");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedWorkerWatchdog.WaitAsync(worker.Task, () => 0, 30000, milliseconds =>
            {
                worker.SetException(failure);
                return Task.CompletedTask;
            }));
        error.Should().BeSameAs(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledTask_PreservesCancellation(bool alreadyCancelled)
    {
        var worker = new TaskCompletionSource<bool>();
        if (alreadyCancelled) worker.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SharedWorkerWatchdog.WaitAsync(worker.Task, () => 0, 30000, milliseconds =>
            {
                worker.SetCanceled();
                return Task.CompletedTask;
            }));
    }

    [Fact]
    public async Task AlreadyCompletedWorker_DoesNotWaitOrReadProgress()
    {
        var result = await SharedWorkerWatchdog.WaitAsync(Task.CompletedTask,
            () => throw new InvalidOperationException("must not read"), 30000,
            milliseconds => throw new InvalidOperationException("must not delay"));
        result.Should().BeTrue();
    }
}
