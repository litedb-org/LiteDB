using System;
using System.Diagnostics;
using System.Threading;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>The recent window covers at least the requested span and at most one minute more (fake clock).</summary>
    public class SharedWaitRecorderWindow_Tests
    {
        private static readonly long Second = Stopwatch.Frequency;
        private static readonly long Minute = 60 * Second;

        private sealed class FakeClock
        {
            internal long Now = 1_000_000_000;
            internal long Read() => Now;
        }

        private static SharedWaitRecorder Recorder(FakeClock clock) =>
            new SharedWaitRecorder("window-test-" + Guid.NewGuid().ToString("N"), "w.db", Timeout.InfiniteTimeSpan, null, clock.Read);

        private static void WaitAt(SharedWaitRecorder recorder, FakeClock clock, long at, long start)
        {
            clock.Now = start + at;
            recorder.End(recorder.Begin(), SharedWaitRecorder.Outcome.Acquired);
        }

        [Fact]
        public void One_minute_window_includes_a_wait_from_just_before_the_boundary()
        {
            var clock = new FakeClock();
            var start = clock.Now;
            var recorder = Recorder(clock);
            WaitAt(recorder, clock, 59 * Second + Second * 9 / 10, start);
            clock.Now = start + 60 * Second + Second / 10;
            var snapshot = recorder.Snapshot(TimeSpan.FromMinutes(1));
            Assert.Equal(1, snapshot.Recent.Count);
            Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(100), snapshot.Window);
        }

        [Fact]
        public void Hour_window_uses_sixty_whole_minutes_plus_the_current_one()
        {
            var clock = new FakeClock();
            var start = clock.Now;
            var recorder = Recorder(clock);
            WaitAt(recorder, clock, Minute / 2, start);              // 60.4 minutes old below: outside the hour
            WaitAt(recorder, clock, Minute * 95 / 100, start);       // 59.95 minutes old: inside
            clock.Now = start + 60 * Minute + Minute * 9 / 10;
            var snapshot = recorder.Snapshot(TimeSpan.FromHours(1));
            // Both fall in minute 0, which the hour window covers in full: at most one minute more.
            Assert.Equal(2, snapshot.Recent.Count);
            Assert.Equal(TimeSpan.FromMinutes(60.9), snapshot.Window);
            // A window longer than an hour is capped at the hour.
            Assert.Equal(snapshot.Window, recorder.Snapshot(TimeSpan.FromHours(3)).Window);

            // Minute 61 reuses minute 0's slot of the 61-minute ring: the old minute is dropped, not merged.
            WaitAt(recorder, clock, 61 * Minute + Minute / 2, start);
            snapshot = recorder.Snapshot(TimeSpan.FromHours(1));
            Assert.Equal(1, snapshot.Recent.Count);
            Assert.Equal(3, snapshot.Total.Count);
        }

        [Fact]
        public void Five_minute_window_reports_the_span_covered()
        {
            var clock = new FakeClock();
            var start = clock.Now;
            var recorder = Recorder(clock);
            WaitAt(recorder, clock, Second, start);
            WaitAt(recorder, clock, 5 * Minute + 10 * Second, start);
            clock.Now = start + 5 * Minute + 30 * Second;
            var snapshot = recorder.Snapshot(TimeSpan.FromMinutes(5));
            // Minutes 0 to 4 whole plus the current partial minute 5.
            Assert.Equal(2, snapshot.Recent.Count);
            Assert.Equal(TimeSpan.FromMinutes(5.5), snapshot.Window);
            // Six minutes later only the minute 5 wait is within five whole minutes plus the current one.
            clock.Now = start + 10 * Minute + 30 * Second;
            Assert.Equal(1, recorder.Snapshot(TimeSpan.FromMinutes(5)).Recent.Count);
        }
    }
}
