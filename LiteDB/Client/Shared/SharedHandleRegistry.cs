using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// The explicit transaction handle of this process that currently owns a Shared database's
    /// native writer mutex (at most one per mutex name), and the async flows that use it.
    /// </summary>
    internal static class SharedHandleRegistry
    {
        private static readonly ConcurrentDictionary<string, SharedHandleActivity> Active =
            new ConcurrentDictionary<string, SharedHandleActivity>(StringComparer.Ordinal);

        // Refusal only, never binding: a marked flow's ordinary calls still never enlist.
        // A weak reference keeps captured execution contexts from rooting a handle.
        private static readonly AsyncLocal<SharedHandleFlow> Flow = new AsyncLocal<SharedHandleFlow>();

        internal static void Register(string mutexName, SharedHandleActivity activity) => Active[mutexName] = activity;

        // Removes only this activity: a later handle's registration is never erased.
        internal static void Unregister(string mutexName, SharedHandleActivity activity) =>
            ((ICollection<KeyValuePair<string, SharedHandleActivity>>)Active)
                .Remove(new KeyValuePair<string, SharedHandleActivity>(mutexName, activity));

        internal static SharedHandleActivity Owner(string mutexName) =>
            Active.TryGetValue(mutexName, out var activity) ? activity : null;

        /// <summary>Remember that the current async flow begins or uses this handle.</summary>
        internal static void Mark(SharedHandleFlow flow)
        {
            if (!ReferenceEquals(Flow.Value, flow)) Flow.Value = flow;
        }

        /// <summary>
        /// Before a Shared call blocks for writer ownership: refuse when the current async flow
        /// holds the active handle that owns it. That flow would have to complete the handle
        /// for the wait to end, so the wait would never end. Other flows wait normally.
        /// </summary>
        internal static bool CurrentFlowHoldsOwner(string mutexName) => CurrentFlowHoldsOwner(mutexName, TimeSpan.Zero);

        /// <summary>As above, when the owning handle has also been idle for at least <paramref name="idle"/>.</summary>
        internal static bool CurrentFlowHoldsOwner(string mutexName, TimeSpan idle)
        {
            if (Active.IsEmpty || !Active.TryGetValue(mutexName, out var owner)) return false;
            var flow = Flow.Value;
            return flow != null && string.Equals(flow.MutexName, mutexName, StringComparison.Ordinal) && flow.IsActive &&
                (idle == TimeSpan.Zero || owner.Idle >= idle);
        }

        internal static InvalidOperationException FlowSelfWait() => new InvalidOperationException(
            "This flow holds an open transaction handle that owns this Shared database's writer ownership. " +
            "Waiting for it here would never end: use the handle's collections, or complete the handle first.");
    }

    /// <summary>Activity of the handle currently owning a Shared writer mutex.</summary>
    internal sealed class SharedHandleActivity
    {
        internal readonly long Acquired = Stopwatch.GetTimestamp();
        private long _lastActive = Stopwatch.GetTimestamp();

        internal void Touch() => Volatile.Write(ref _lastActive, Stopwatch.GetTimestamp());

        internal TimeSpan Held => Since(Acquired);
        internal TimeSpan Idle => Since(Volatile.Read(ref _lastActive));

        private static TimeSpan Since(long timestamp) =>
            TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency);
    }

    /// <summary>The marker an async flow carries for the Shared handle it uses.</summary>
    internal sealed class SharedHandleFlow
    {
        internal readonly string MutexName;
        private readonly WeakReference<LiteTransaction> _handle;

        internal SharedHandleFlow(string mutexName, LiteTransaction handle)
        {
            MutexName = mutexName;
            _handle = new WeakReference<LiteTransaction>(handle);
        }

        internal bool IsActive => _handle.TryGetTarget(out var handle) && handle.State == LiteTransactionState.Active;
    }
}
