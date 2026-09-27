#if NET8_0_OR_GREATER
using System.Diagnostics;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedReadPacer_Tests
    {
        [Fact]
        public void A_long_read_cannot_schedule_an_unbounded_pause()
        {
            var pacer = new SharedReadPacer();
            pacer.RecordWork(long.MaxValue);
            var total = 0;
            for (var i = 0; i < 20; i++)
            {
                var delay = pacer.ReserveDelay(true);
                delay.Should().BeInRange(0, 10);
                total += delay;
            }
            total.Should().Be(50);
        }

        [Fact]
        public void Writer_departure_clears_pending_work_instead_of_delaying_idle_readers()
        {
            var pacer = new SharedReadPacer();
            pacer.RecordWork(Stopwatch.Frequency);
            pacer.ReserveDelay(false).Should().Be(0);
            pacer.ReserveDelay(true).Should().Be(0);
        }

        [Fact]
        public void Coarse_timer_delays_are_credited_but_a_suspension_cannot_disable_future_pacing()
        {
            var pacer = new SharedReadPacer();
            pacer.RecordWork(Stopwatch.Frequency / 1000);
            var reserved = pacer.ReserveDelay(true);
            reserved.Should().BeGreaterThan(0);
            pacer.RecordDelay(reserved, long.MaxValue);
            pacer.ReserveDelay(true).Should().Be(0);
            pacer.RecordWork(Stopwatch.Frequency);
            pacer.ReserveDelay(true).Should().BeGreaterThan(0);
        }
    }
}
#endif
