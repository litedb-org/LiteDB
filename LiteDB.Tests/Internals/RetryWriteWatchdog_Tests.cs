#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class RetryWriteWatchdog_Tests
    {
        [Fact]
        public async Task Stalled_child_fails_after_last_completed_write()
        {
            var directory = Path.Combine(Path.GetTempPath(), "retry-watchdog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var child = new MvccProcess("insert-progress", Path.Combine(directory, "test.db"), null,
                    "3000", testingHook: "stall:3004");
                Func<Task> wait = () => RetryWriteWatchdog.Wait(child, 3000, 20);
                var failure = await wait.Should().ThrowAsync<InvalidOperationException>();
                failure.Which.Message.Should().Contain("last completed step=write:3004");
                failure.Which.InnerException.Should().BeOfType<TimeoutException>();
                // Disposal happens only after the failure retained its progress evidence.
            }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
        }

        [Fact]
        public async Task Progressing_work_can_exceed_a_single_stall_window()
        {
            var step = 0;
            async Task<string> Read(TimeSpan timeout)
            {
                await Task.Delay(100).WaitAsync(timeout);
                return step < 10 ? "write:" + step++ : "done";
            }
            await RetryWriteWatchdog.Wait(Read, 0, 10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            step.Should().Be(10);
        }

        [Fact]
        public async Task Overall_cap_bounds_even_progressing_work()
        {
            var step = 0;
            async Task<string> Read(TimeSpan timeout)
            {
                await Task.Delay(50).WaitAsync(timeout);
                return "write:" + step++;
            }
            Func<Task> wait = () => RetryWriteWatchdog.Wait(Read, 0, 1000,
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));
            var failure = await wait.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.InnerException.Should().BeOfType<TimeoutException>();
            step.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData("done")]
        [InlineData("write:2999")]
        [InlineData(null)]
        public async Task Missing_or_unrelated_progress_fails(string line)
        {
            Func<Task> wait = () => RetryWriteWatchdog.Wait(_ => Task.FromResult(line), 3000, 20,
                RetryWriteWatchdog.StallWindow, RetryWriteWatchdog.SafetyCap);
            var failure = await wait.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        }
    }
}
#endif
