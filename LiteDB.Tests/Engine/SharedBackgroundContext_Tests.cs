using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedBackgroundContext_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Infrastructure_timer_and_thread_do_not_capture_request_context_and_restore_caller_flow(bool suppressed)
        {
            var ambient = new AsyncLocal<object> { Value = new object() };
            var flow = default(AsyncFlowControl);
            if (suppressed) flow = ExecutionContext.SuppressFlow();
            try
            {
                using var tick = new ManualResetEventSlim();
                object timerValue = ambient.Value;
                object threadValue = ambient.Value;
                using var timer = SharedBackground.CreateTimer(_ => { timerValue = ambient.Value; tick.Set(); },
                    null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                var thread = new Thread(() => threadValue = ambient.Value);
                SharedBackground.Start(thread);
                thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                tick.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                timerValue.Should().BeNull();
                threadValue.Should().BeNull();
                ambient.Value.Should().NotBeNull();
                ExecutionContext.IsFlowSuppressed().Should().Be(suppressed);
            }
            finally
            {
                if (suppressed) flow.Undo();
                ambient.Value = null;
            }
        }
    }
}
