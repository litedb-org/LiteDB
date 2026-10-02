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

    /// <summary>
    /// The bound of one wait. Only a bound that ends the wait without progress of the cycle's own
    /// threads makes a cycle <see cref="WaitRule.BoundedCycle"/>: a timeout, or a cancellation that
    /// has been requested (<see cref="EndsByItself"/>). A cancellation nobody requested yet is not a
    /// bound for classification: whether it ever comes is the application's choice (a close, a
    /// cancel), so the cycle hangs until then, exactly like an unbounded one. A token whose source
    /// is linked to a timeout (<c>CancelAfter</c>) is a timeout: declare it with <see cref="After"/>.
    /// </summary>
    internal readonly struct WaitBound
    {
        private WaitBound(WaitBoundKind kind, TimeSpan timeout, System.Threading.CancellationToken token = default)
        {
            this.Kind = kind;
            this.Timeout = timeout;
            this.Token = token;
        }

        internal WaitBoundKind Kind { get; }

        /// <summary>The timeout, for <see cref="WaitBoundKind.Timeout"/>.</summary>
        internal TimeSpan Timeout { get; }

        /// <summary>The token, for <see cref="WaitBoundKind.Cancellation"/> declared with <see cref="CancelledBy"/>.</summary>
        internal System.Threading.CancellationToken Token { get; }

        internal static WaitBound Unbounded => default;

        /// <summary>A cancellable wait whose token the site does not pass: never counted as requested.</summary>
        internal static WaitBound Cancellation => new WaitBound(WaitBoundKind.Cancellation, TimeSpan.Zero);

        /// <summary>A wait that ends when <paramref name="token"/> is cancelled; a token that cannot be cancelled is unbounded.</summary>
        internal static WaitBound CancelledBy(System.Threading.CancellationToken token) =>
            token.CanBeCanceled ? new WaitBound(WaitBoundKind.Cancellation, TimeSpan.Zero, token) : Unbounded;

        /// <summary>A wait that gives up after <paramref name="timeout"/>; an infinite timeout is unbounded.</summary>
        internal static WaitBound After(TimeSpan timeout) =>
            timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout == TimeSpan.MaxValue
                ? Unbounded
                : new WaitBound(WaitBoundKind.Timeout, timeout);

        /// <summary>
        /// Whether the wait ends without any thread of its cycle making progress: a timeout, or a
        /// cancellation already requested. Read when a cycle is classified.
        /// </summary>
        internal bool EndsByItself =>
            this.Kind == WaitBoundKind.Timeout ||
            (this.Kind == WaitBoundKind.Cancellation && this.Token.IsCancellationRequested);

        public override string ToString()
        {
            switch (this.Kind)
            {
                case WaitBoundKind.Timeout: return "timeout " + this.Timeout.TotalMilliseconds + " ms";
                case WaitBoundKind.Cancellation:
                    return this.Token.IsCancellationRequested ? "cancellation, requested" : "cancellation, not requested";
                default: return "unbounded";
            }
        }
    }

    /// <summary>How a reported cycle or order is classified. Each rule reports by default.</summary>
    internal enum WaitRule
    {
        /// <summary>
        /// The holder executes on the waiting thread (length 1), and the edge is not a recursion the
        /// primitive grants. No progress of another thread can end it. Candidate failure.
        /// </summary>
        SelfWait,
        /// <summary>
        /// A cycle across threads that no wait of it ends by itself: every wait is unbounded or only
        /// cancellable, with no cancellation requested. Candidate failure.
        /// </summary>
        UnboundedCycle,
        /// <summary>
        /// A cycle across threads with a wait that ends by itself (a timeout, or a requested
        /// cancellation): allowed when the outcomes are correct (a lock timeout is LiteDB's
        /// documented resolution, for example crossed collection locks).
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
