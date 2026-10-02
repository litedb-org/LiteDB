#if DEBUG || TESTING
using System;

namespace LiteDB.Utils
{
    /// <summary>The synchronization primitive behind a wait-for graph resource; it decides recursion.</summary>
    internal enum WaitPrimitive
    {
        /// <summary>A monitor (<c>lock</c>, <c>Monitor.TryEnter</c>); recursive for its owning thread.</summary>
        Monitor,
        /// <summary><c>System.Threading.Lock</c>; recursive for its owning thread.</summary>
        Lock,
        /// <summary>A named OS <c>Mutex</c>; recursive for the thread that acquired it.</summary>
        NamedMutex,
        /// <summary>A <c>SemaphoreSlim</c>; not recursive.</summary>
        SemaphoreSlim,
        /// <summary>A Shared connection's logical ownership; its owner thread re-enters without waiting.</summary>
        Ownership,
        /// <summary>The engine's transaction admission gate (leases and the exclusive writer).</summary>
        Gate,
        /// <summary>A reader or snapshot lease.</summary>
        Lease,
        /// <summary>A Shared pin: a holder thread keeps the mutex for an owner thread.</summary>
        Pin,
        /// <summary>A command handed to a helper thread, waited for by its sender.</summary>
        Handoff,
        /// <summary>A <c>ManualResetEvent(Slim)</c> or <c>AutoResetEvent</c> some thread owes a signal.</summary>
        Event,
        /// <summary>A <c>Monitor.Wait</c> on a predicate that other threads' holds keep false.</summary>
        Condition,
        /// <summary><c>Thread.Join</c>: the joined thread's progress.</summary>
        ThreadJoin,
        /// <summary><c>Task.Wait</c> or <c>.Result</c>.</summary>
        TaskWait,
        /// <summary>A barrier between test-driver threads.</summary>
        Barrier,
    }

    /// <summary>How a wait can end without the resource: never, by a timeout, or by cancellation.</summary>
    internal enum WaitBoundKind
    {
        Unbounded,
        Timeout,
        Cancellation,
    }

    /// <summary>Whether an edge comes from the library or from a test driver (callbacks, joins, barriers).</summary>
    internal enum WaitOrigin
    {
        Library,
        Driver,
    }

    /// <summary>The bound of one wait.</summary>
    internal readonly struct WaitBound
    {
        private WaitBound(WaitBoundKind kind, TimeSpan timeout)
        {
            this.Kind = kind;
            this.Timeout = timeout;
        }

        internal WaitBoundKind Kind { get; }

        /// <summary>The timeout, for <see cref="WaitBoundKind.Timeout"/>.</summary>
        internal TimeSpan Timeout { get; }

        internal static WaitBound Unbounded => default;

        internal static WaitBound Cancellation => new WaitBound(WaitBoundKind.Cancellation, TimeSpan.Zero);

        /// <summary>A wait that gives up after <paramref name="timeout"/>; an infinite timeout is unbounded.</summary>
        internal static WaitBound After(TimeSpan timeout) =>
            timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout == TimeSpan.MaxValue
                ? Unbounded
                : new WaitBound(WaitBoundKind.Timeout, timeout);

        public override string ToString() => this.Kind == WaitBoundKind.Timeout
            ? "timeout " + this.Timeout.TotalMilliseconds + " ms"
            : this.Kind.ToString().ToLowerInvariant();
    }

    /// <summary>How a reported cycle or order is classified. Each rule reports by default.</summary>
    internal enum WaitRule
    {
        /// <summary>
        /// The holder executes on the waiting thread (length 1), and the edge is not a recursion the
        /// primitive grants. No progress of another thread can end it. Candidate failure.
        /// </summary>
        SelfWait,
        /// <summary>A cycle across threads whose every wait is unbounded. Candidate failure.</summary>
        UnboundedCycle,
        /// <summary>
        /// A cycle across threads with at least one bounded wait: allowed when the outcomes are correct
        /// (a lock timeout is LiteDB's documented resolution, for example crossed collection locks).
        /// </summary>
        BoundedCycle,
        /// <summary>Lock order A then B on one thread, B then A seen on another thread, without an active cycle.</summary>
        LockOrder,
    }

    internal static class WaitPrimitiveExtensions
    {
        /// <summary>Whether a thread that holds the primitive acquires it again without waiting.</summary>
        internal static bool IsRecursive(this WaitPrimitive primitive) =>
            primitive == WaitPrimitive.Monitor || primitive == WaitPrimitive.Lock ||
            primitive == WaitPrimitive.NamedMutex || primitive == WaitPrimitive.Ownership;

        /// <summary>Stable rule id, as used by <c>LITEDB_WAITGRAPH_FAIL</c> and reports.</summary>
        internal static string Id(this WaitRule rule)
        {
            switch (rule)
            {
                case WaitRule.SelfWait: return "self-wait";
                case WaitRule.UnboundedCycle: return "unbounded-cycle";
                case WaitRule.BoundedCycle: return "bounded-cycle";
                default: return "lock-order";
            }
        }
    }
}
#endif
