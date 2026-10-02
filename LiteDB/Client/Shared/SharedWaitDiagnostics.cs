using System;

namespace LiteDB
{
    /// <summary>Who currently owns a Shared database's writer mutex, as far as this process knows.</summary>
    public enum SharedWriterOwner
    {
        /// <summary>No owner in this process is known; it may be free, another connection or another process.</summary>
        Unknown,

        /// <summary>An explicit transaction handle (<see cref="ILiteTransaction"/>) of this process.</summary>
        TransactionHandle
    }

    /// <summary>Aggregated waits for Shared writer ownership over some period.</summary>
    public sealed class SharedWaitStatistics
    {
        internal SharedWaitStatistics(long count, TimeSpan total, TimeSpan max, long over500Milliseconds, long over1Second, long timedOut, long refused)
        {
            Count = count;
            TotalWait = total;
            MaxWait = max;
            Over500Milliseconds = over500Milliseconds;
            Over1Second = over1Second;
            TimedOut = timedOut;
            Refused = refused;
        }

        /// <summary>
        /// Completed waits for ownership: acquisitions (including immediate ones, and ones whose
        /// call failed otherwise) and timeouts. Refused waits are not included; see <see cref="Refused"/>.
        /// </summary>
        public long Count { get; }

        /// <summary>Summed duration of the waits in <see cref="Count"/>.</summary>
        public TimeSpan TotalWait { get; }

        /// <summary>Longest completed wait.</summary>
        public TimeSpan MaxWait { get; }

        /// <summary>Completed waits that took longer than 500 milliseconds.</summary>
        public long Over500Milliseconds { get; }

        /// <summary>Completed waits that took longer than one second.</summary>
        public long Over1Second { get; }

        /// <summary>Waits that ran out of <c>SharedWriterTimeout</c>.</summary>
        public long TimedOut { get; }

        /// <summary>
        /// Waits refused (SharedSelfWaitGrace) because their own flow held the idle owning handle,
        /// at once or after a grace. They add nothing to <see cref="Count"/>, <see cref="TotalWait"/> or <see cref="MaxWait"/>.
        /// </summary>
        public long Refused { get; }
    }

    /// <summary>A snapshot of one Shared connection's writer-ownership waits.</summary>
    public sealed class SharedWaitDiagnostics
    {
        internal SharedWaitDiagnostics(int currentWaiters, TimeSpan longestCurrentWait, SharedWriterOwner owner,
            TimeSpan ownerHeld, TimeSpan ownerIdle, TimeSpan window, SharedWaitStatistics recent, SharedWaitStatistics total)
        {
            CurrentWaiters = currentWaiters;
            LongestCurrentWait = longestCurrentWait;
            Owner = owner;
            OwnerHeld = ownerHeld;
            OwnerIdle = ownerIdle;
            Window = window;
            Recent = recent;
            Total = total;
        }

        /// <summary>Calls of this connection waiting now.</summary>
        public int CurrentWaiters { get; }

        /// <summary>How long the oldest current wait has lasted so far.</summary>
        public TimeSpan LongestCurrentWait { get; }

        /// <summary>The owner this process knows of.</summary>
        public SharedWriterOwner Owner { get; }

        /// <summary>For a <see cref="SharedWriterOwner.TransactionHandle"/> owner: how long it has owned the mutex.</summary>
        public TimeSpan OwnerHeld { get; }

        /// <summary>For a <see cref="SharedWriterOwner.TransactionHandle"/> owner: time since its last operation.</summary>
        public TimeSpan OwnerIdle { get; }

        /// <summary>
        /// The period <see cref="Recent"/> covers: the current partial minute plus the requested window
        /// rounded up to whole minutes (at most one hour), so at least the window and at most one minute more.
        /// </summary>
        public TimeSpan Window { get; }

        /// <summary>Waits completed within <see cref="Window"/>.</summary>
        public SharedWaitStatistics Recent { get; }

        /// <summary>Waits completed since the connection was created.</summary>
        public SharedWaitStatistics Total { get; }
    }

    /// <summary>One completed wait that reached <c>SharedSlowWaitThreshold</c>.</summary>
    public sealed class SharedSlowWait
    {
        internal SharedSlowWait(string filename, TimeSpan elapsed, bool timedOut, SharedWriterOwner owner)
        {
            Filename = filename;
            Elapsed = elapsed;
            TimedOut = timedOut;
            Owner = owner;
        }

        /// <summary>The database file.</summary>
        public string Filename { get; }

        /// <summary>How long the wait lasted.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Whether the wait ended by timing out rather than acquiring ownership.</summary>
        public bool TimedOut { get; }

        /// <summary>The owner this process knew of when the wait began.</summary>
        public SharedWriterOwner Owner { get; }
    }
}
