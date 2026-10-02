using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Safety
{
    /// <summary>One operation in flight, as the deadline watchdog sees it.</summary>
    internal sealed class InFlightOperation
    {
        public long Id { get; set; }
        public string Operation { get; set; }
        public string Dimension { get; set; }
        public int Step { get; set; }
        public int ManagedThreadId { get; set; }
        public string ThreadName { get; set; }
        public long StartedTicks { get; set; }
        public TimeSpan Deadline { get; set; }
        /// <summary>Set once the watchdog reported this operation as overdue.</summary>
        public bool Reported { get; set; }
        public double ElapsedMs => (Stopwatch.GetTimestamp() - this.StartedTicks) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Deadline oracle: every operation of a bounded scenario completes or throws within the
    /// deadline the scenario declared for it. Each operation's clock starts when it begins on its
    /// caller's thread and is never refreshed by other operations, other threads or harness
    /// heartbeats, so a stalled actor is reported while others keep making progress. The watchdog
    /// thread reports the first overdue operation once, with a snapshot of every operation in
    /// flight on every thread. The caller decides what an overdue operation means (a fuzz child
    /// records a replayable failure and terminates; a test fails). A deadline is a harness bound
    /// for one scenario, not a public-API guarantee.
    /// </summary>
    internal sealed class DeadlineWatchdog : IDisposable
    {
        /// <summary>Floor of the default deadline for lock-bound operations.</summary>
        public static readonly TimeSpan MinimumLockBound = TimeSpan.FromSeconds(15);
        private readonly ConcurrentDictionary<long, InFlightOperation> _inFlight = new ConcurrentDictionary<long, InFlightOperation>();
        private readonly Action<InFlightOperation, InFlightOperation[]> _overdue;
        private readonly TimeSpan _poll;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private long _next;
        private int _reported;

        public DeadlineWatchdog(Action<InFlightOperation, InFlightOperation[]> overdue, TimeSpan? poll = null)
        {
            _overdue = overdue;
            _poll = poll ?? TimeSpan.FromMilliseconds(100);
            _thread = new Thread(this.Watch) { IsBackground = true, Name = "safety deadline watchdog" };
            _thread.Start();
        }

        /// <summary>
        /// Default deadline of an operation whose only waits are lock waits bounded by the TIMEOUT
        /// pragma: max(3 x <paramref name="timeout"/>, 15 s). Bulk, rebuild and callback operations
        /// declare their own. Oracle self-tests lower the floor with <paramref name="floor"/>.
        /// </summary>
        public static TimeSpan LockBound(TimeSpan timeout, TimeSpan? floor = null) =>
            TimeSpan.FromTicks(Math.Max(timeout.Ticks * 3, (floor ?? MinimumLockBound).Ticks));

        /// <summary>A declared deadline kept below <paramref name="cap"/> (the runner's anonymous hang watchdog).</summary>
        public static TimeSpan Capped(TimeSpan deadline, TimeSpan? cap) =>
            cap.HasValue && cap.Value > TimeSpan.Zero && deadline > cap.Value ? cap.Value : deadline;

        public bool Reported => Volatile.Read(ref _reported) != 0;

        public InFlightOperation Begin(string operation, string dimension, int step, TimeSpan deadline)
        {
            var thread = Thread.CurrentThread;
            var item = new InFlightOperation
            {
                Id = Interlocked.Increment(ref _next), Operation = operation, Dimension = dimension, Step = step,
                ManagedThreadId = thread.ManagedThreadId, ThreadName = thread.Name, Deadline = deadline,
                StartedTicks = Stopwatch.GetTimestamp()
            };
            _inFlight[item.Id] = item;
            return item;
        }

        public void End(InFlightOperation item) => _inFlight.TryRemove(item.Id, out _);

        public InFlightOperation[] Snapshot() => _inFlight.Values.OrderBy(item => item.Id).ToArray();

        private void Watch()
        {
            while (!_stop.Wait(_poll))
            {
                var overdue = _inFlight.Values.Where(item => item.ElapsedMs > item.Deadline.TotalMilliseconds)
                    .OrderBy(item => item.StartedTicks).FirstOrDefault();
                if (overdue == null || Interlocked.Exchange(ref _reported, 1) != 0) continue;
                overdue.Reported = true;
                _overdue(overdue, this.Snapshot());
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (!ReferenceEquals(Thread.CurrentThread, _thread)) _thread.Join();
        }
    }
}
