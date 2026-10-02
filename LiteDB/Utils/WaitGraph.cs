#if DEBUG || TESTING
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LiteDB.Utils
{
    /// <summary>
    /// Test-build wait-for graph: typed dependency instrumentation in reporting mode. Blocking sites
    /// register their wait (primitive, bound, origin) before they block; acquisitions and releases
    /// register holds; frames tell which owner's work a thread executes now. Before a thread blocks, a
    /// search looks for a cycle back to it and classifies it (<see cref="WaitRule"/>); a lock-order
    /// history is kept as well. Findings are latched in a side channel (<see cref="Findings"/>, and
    /// <c>LITEDB_WAITGRAPH_REPORT</c>) and reported at the end of a test or scenario. Nothing is ever
    /// thrown through library code. A harness fails on a finding only for rules configured to fail
    /// (<c>LITEDB_WAITGRAPH_FAIL</c>, <see cref="SetFailing"/>); all rules report by default.
    /// See docs/wait-for-graph.md. <c>LITEDB_WAITGRAPH=0</c> disables recording.
    /// </summary>
    internal static partial class WaitGraph
    {
        private static readonly bool _environmentEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("LITEDB_WAITGRAPH"), "0", StringComparison.Ordinal);
        private static volatile bool _forced;

        private static readonly ConcurrentDictionary<string, Resource> _named =
            new ConcurrentDictionary<string, Resource>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, Resource> _bound = new ConditionalWeakTable<object, Resource>();
        private static readonly ConditionalWeakTable<Thread, ThreadState> _states = new ConditionalWeakTable<Thread, ThreadState>();
        // Owners whose every hold a frame claims (a teardown runs under all of them), by owner.
        private static readonly Dictionary<object, List<ThreadState>> _claims =
            new Dictionary<object, List<ThreadState>>(IdentityComparer.Instance);

        [ThreadStatic] private static ThreadState _current;

        /// <summary>Whether hooks record anything.</summary>
        internal static bool Enabled => _environmentEnabled || _forced;

        /// <summary>Test hook: enable the graph for the graph's own tests even when the environment disabled it.</summary>
        internal static IDisposable Force()
        {
            var previous = _forced;
            _forced = true;
            return new Restore(() => _forced = previous);
        }

        private static ThreadState Current => _current ?? (_current = StateOf(Thread.CurrentThread));

        private static ThreadState StateOf(Thread thread)
        {
            lock (_states) return _states.GetValue(thread, t => new ThreadState(t));
        }

        /// <summary>A new resource for a primitive the caller owns.</summary>
        internal static Resource Create(string kind, WaitPrimitive primitive, string label = null, bool ordered = true) =>
            new Resource(kind, label, primitive, ordered);

        /// <summary>The process-wide resource <paramref name="label"/> of <paramref name="kind"/>, such as a named OS mutex.</summary>
        internal static Resource Named(string kind, string label, WaitPrimitive primitive) =>
            _named.GetOrAdd(kind + ":" + label, _ => new Resource(kind, label, primitive));

        /// <summary>Make <paramref name="key"/> (for example a Mutex instance) stand for <paramref name="resource"/>.</summary>
        internal static void Bind(object key, Resource resource)
        {
            lock (_bound)
            {
                _bound.Remove(key);
                _bound.Add(key, resource);
            }
        }

        /// <summary>The resource bound to <paramref name="key"/>, else one of <paramref name="kind"/> for that object.</summary>
        internal static Resource Of(object key, string kind, WaitPrimitive primitive)
        {
            lock (_bound) return _bound.GetValue(key, _ => new Resource(kind, null, primitive));
        }

        /// <summary>
        /// Record, after the real acquisition, that <paramref name="owner"/> holds <paramref name="resource"/>
        /// on this thread. Without an owner the hold is the calling thread's own (thread-affine).
        /// </summary>
        internal static void Acquired(Resource resource, object owner = null, bool threadAffine = false, string site = null)
        {
            if (!Enabled || resource == null) return;
            var state = Current;
            if (owner == null || ReferenceEquals(owner, Thread.CurrentThread))
            {
                // A thread's own hold is keyed by its graph state, never by the Thread object.
                owner = state;
                threadAffine = true;
            }
            else if (owner is Thread other) owner = StateOf(other);
            if (!resource.Add(owner, state, threadAffine, site)) return;
            var held = state.Remember(resource);
            if (resource.Ordered) RecordOrder(state, held, resource, site);
        }

        /// <summary>
        /// Record, before the real release, that one acquisition of <paramref name="owner"/> (default:
        /// the calling thread) ended, or all of them. Any thread may call it.
        /// </summary>
        internal static void Released(Resource resource, object owner = null, bool all = false)
        {
            if (!Enabled || resource == null) return;
            resource.Remove(owner == null ? Current : owner is Thread thread ? StateOf(thread) : owner, all);
        }

        /// <summary>End every hold of <paramref name="resource"/>, as disposing it does.</summary>
        internal static void ReleaseAll(Resource resource)
        {
            if (!Enabled || resource == null) return;
            resource.Clear();
        }

        /// <summary>
        /// The calling thread starts executing work of <paramref name="owner"/> (a public call, a
        /// read, a teardown). With <paramref name="claimsAll"/>, every hold of the owner, on any
        /// thread, ends only after this frame does.
        /// </summary>
        internal static void Enter(object owner, bool claimsAll = false)
        {
            if (!Enabled || owner == null) return;
            var state = Current;
            state.Enter(owner, claimsAll);
            if (!claimsAll) return;
            lock (_claims)
            {
                if (!_claims.TryGetValue(owner, out var list)) _claims[owner] = list = new List<ThreadState>(1);
                list.Add(state);
            }
        }

        /// <summary>End the innermost frame of <paramref name="owner"/> on the calling thread.</summary>
        internal static void Exit(object owner)
        {
            if (!Enabled || owner == null) return;
            var state = Current;
            var frame = state.Exit(owner);
            if (frame == null || !frame.ClaimsAll) return;
            lock (_claims)
            {
                if (!_claims.TryGetValue(owner, out var list)) return;
                list.Remove(state);
                if (list.Count == 0) _claims.Remove(owner);
            }
        }

        /// <summary>Frame scope for a <c>using</c> statement.</summary>
        internal static Restore Executing(object owner, bool claimsAll = false)
        {
            Enter(owner, claimsAll);
            return new Restore(() => Exit(owner));
        }

        /// <summary>
        /// Register that the calling thread is about to block on <paramref name="resource"/> (and
        /// <paramref name="also"/>), then search for a cycle and latch what it finds. Never throws.
        /// <paramref name="viaHandoff"/>: a helper thread performs the primitive wait for this thread.
        /// <paramref name="excludeOwn"/>: the wait does not wait for the waiting thread's own holds.
        /// Dispose the scope as soon as the blocking call returns.
        /// </summary>
        internal static WaitScope Wait(Resource resource, WaitBound bound, string site, object owner = null,
            Resource also = null, bool excludeOwn = false, bool viaHandoff = false, WaitOrigin origin = WaitOrigin.Library)
        {
            if (!Enabled || resource == null) return default;
            var state = Current;
            var record = new WaitRecord(resource, also, bound, origin, viaHandoff, site, owner, excludeOwn, state.Wait);
            state.Push(record);
            Check(state, record);
            return new WaitScope(state, record);
        }

        /// <summary>Search again for the innermost registered wait; call on each iteration of a poll loop.</summary>
        internal static void Recheck()
        {
            if (!Enabled) return;
            var state = Current;
            var record = state.Wait;
            if (record != null) Check(state, record);
        }

        /// <summary>Driver edge: a test driver waits on <paramref name="resource"/> (an event, barrier or callback).</summary>
        internal static WaitScope DriverWait(Resource resource, WaitBound bound, string site) =>
            Wait(resource, bound, site, origin: WaitOrigin.Driver);

        /// <summary>Driver edge: the calling thread joins <paramref name="thread"/> and waits for its progress.</summary>
        internal static WaitScope Join(Thread thread, WaitBound bound, string site) =>
            Enabled ? Wait(StateOf(thread).Progress, bound, site, StateOf(thread), origin: WaitOrigin.Driver) : default;

        internal static string Describe(object owner)
        {
            if (owner == null) return "nobody";
            if (owner is Thread thread)
                return $"thread '{thread.Name ?? "unnamed"}' #{thread.ManagedThreadId}";
            if (owner is ThreadState state) return state.ToString();
            return owner.GetType().Name + "#" + _ids.GetValue(owner, _ => new StrongBox<int>(Interlocked.Increment(ref _nextOwnerId))).Value;
        }

        private static readonly ConditionalWeakTable<object, StrongBox<int>> _ids = new ConditionalWeakTable<object, StrongBox<int>>();
        private static int _nextOwnerId;

        private static ThreadState[] Claimers(object owner)
        {
            lock (_claims) return _claims.TryGetValue(owner, out var list) ? list.ToArray() : Array.Empty<ThreadState>();
        }

        /// <summary>Disposable that runs an action once.</summary>
        internal sealed class Restore : IDisposable
        {
            private Action _action;

            internal Restore(Action action) => _action = action;

            public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            internal static readonly IdentityComparer Instance = new IdentityComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif
