#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteDB.Utils
{
    internal static partial class WaitGraph
    {
        private static int _nextResourceId;

        /// <summary>
        /// Something a thread can wait for: a lock, a gate, a named OS mutex, a handoff. Its holds
        /// say who must make progress before a waiter can. Holds change under the resource's own
        /// monitor, a leaf lock that is never held while another is taken.
        /// </summary>
        internal sealed class Resource
        {
            internal readonly int Id;
            internal readonly string Kind;
            internal readonly List<Hold> Holds = new List<Hold>(1);
            // Bumped (under the monitor) whenever a hold ends, so a search can prove that every
            // hold it followed still existed when it finished.
            internal int Version;

            internal Resource(string kind, string label, WaitPrimitive primitive, bool ordered = true)
            {
                this.Id = Interlocked.Increment(ref _nextResourceId);
                this.Kind = kind;
                this.Label = label;
                this.Primitive = primitive;
                this.Ordered = ordered;
            }

            /// <summary>The primitive behind the resource, which decides whether its holder re-enters without waiting.</summary>
            internal WaitPrimitive Primitive { get; }

            /// <summary>Stable part of the name, such as a collection or mutex name; may be set once known.</summary>
            internal string Label { get; set; }

            /// <summary>Whether acquisitions take part in lock-order tracking (rule d). Events and counters do not.</summary>
            internal bool Ordered { get; }

            public override string ToString() =>
                this.Label == null ? $"{this.Kind}#{this.Id}" : $"{this.Kind} '{this.Label}'#{this.Id}";

            /// <summary>Add one acquisition; true when this created a new hold.</summary>
            internal bool Add(object owner, ThreadState thread, bool threadAffine, string site)
            {
                lock (this)
                {
                    foreach (var hold in this.Holds)
                    {
                        if (!ReferenceEquals(hold.Owner, owner) || !ReferenceEquals(hold.Thread, thread)) continue;
                        hold.Count++;
                        return false;
                    }
                    this.Holds.Add(new Hold(owner, thread, threadAffine, site));
                    return true;
                }
            }

            /// <summary>End one acquisition of <paramref name="owner"/>, or all of them.</summary>
            internal void Remove(object owner, bool all)
            {
                Hold removed = null;
                lock (this)
                {
                    for (var i = 0; i < this.Holds.Count; i++)
                    {
                        var hold = this.Holds[i];
                        if (!ReferenceEquals(hold.Owner, owner)) continue;
                        if (!all && --hold.Count > 0) return;
                        this.Holds.RemoveAt(i);
                        this.Version++;
                        removed = hold;
                        break;
                    }
                }
                removed?.Thread.Forget(this);
            }

            internal void Clear()
            {
                Hold[] removed;
                lock (this)
                {
                    removed = this.Holds.ToArray();
                    this.Holds.Clear();
                    this.Version++;
                }
                foreach (var hold in removed) hold.Thread.Forget(this);
            }

            internal Hold[] Snapshot(out int version)
            {
                lock (this)
                {
                    version = this.Version;
                    return this.Holds.Count == 0 ? Array.Empty<Hold>() : this.Holds.ToArray();
                }
            }
        }

        /// <summary>
        /// A resource held by <see cref="Owner"/>, recorded on <see cref="Thread"/>. A thread-affine
        /// hold (a monitor, a directly owned OS mutex, a lease keyed by its thread) can only end by
        /// that thread's progress. Any other hold ends by its owner's progress, which runs on
        /// <see cref="Thread"/> only while that thread executes a frame of the owner.
        /// </summary>
        internal sealed class Hold
        {
            internal readonly object Owner;
            internal readonly ThreadState Thread;
            internal readonly bool ThreadAffine;
            internal readonly string Site;
            internal int Count = 1;

            internal Hold(object owner, ThreadState thread, bool threadAffine, string site)
            {
                this.Owner = owner;
                this.Thread = thread;
                this.ThreadAffine = threadAffine;
                this.Site = site;
            }
        }

        /// <summary>
        /// One blocking wait, innermost on top of its thread's stack. A wait <see cref="ViaHandoff"/> is
        /// performed by a helper thread for this one, so the primitive's recursion does not apply to it.
        /// </summary>
        internal sealed class WaitRecord
        {
            internal readonly Resource First;
            internal readonly Resource Second;
            internal readonly WaitBound Bound;
            internal readonly WaitOrigin Origin;
            internal readonly bool ViaHandoff;
            internal readonly string Site;
            internal readonly object Owner;
            internal readonly bool ExcludeOwn;
            internal readonly WaitRecord Outer;
            internal readonly DateTime Started = DateTime.UtcNow;

            internal WaitRecord(Resource first, Resource second, WaitBound bound, WaitOrigin origin, bool viaHandoff,
                string site, object owner, bool excludeOwn, WaitRecord outer)
            {
                this.First = first;
                this.Second = second;
                this.Bound = bound;
                this.Origin = origin;
                this.ViaHandoff = viaHandoff;
                this.Site = site;
                this.Owner = owner;
                this.ExcludeOwn = excludeOwn;
                this.Outer = outer;
            }
        }

        /// <summary>An owner whose work runs on the frame's thread now; immutable, innermost first.</summary>
        internal sealed class Frame
        {
            internal readonly object Owner;
            internal readonly bool ClaimsAll;
            internal readonly Frame Outer;

            internal Frame(object owner, bool claimsAll, Frame outer)
            {
                this.Owner = owner;
                this.ClaimsAll = claimsAll;
                this.Outer = outer;
            }
        }

        /// <summary>
        /// Per-thread waits and frames. Only the thread itself changes them; other threads read
        /// them during a search. <see cref="Version"/> is odd while a wait or frame is removed.
        /// </summary>
        internal sealed class ThreadState
        {
            private static int _nextId;
            // Weak: the graph must not keep a dead thread (and its execution context) alive.
            private readonly WeakReference<Thread> _thread;
            private readonly int _managedId;
            /// <summary>Process-unique id; the lock-order history keeps this, not the state.</summary>
            internal readonly int Id = Interlocked.Increment(ref _nextId);
            // Weak: a hold that is never released (an abandoned transaction) must not let a live thread
            // root its resource, the resource's holds and their owners (the lock-order history only).
            private readonly List<WeakReference<Resource>> _held = new List<WeakReference<Resource>>();
            private volatile WaitRecord _wait;
            private volatile Frame _frames;
            internal int Version;

            internal ThreadState(Thread thread)
            {
                _thread = new WeakReference<Thread>(thread);
                _managedId = thread.ManagedThreadId;
                // Joining a thread waits for its progress, which only that thread makes.
                this.Progress = new Resource("thread-progress", null, WaitPrimitive.ThreadJoin, ordered: false);
                this.Progress.Add(this, this, threadAffine: true, site: "thread start");
            }

            /// <summary>What a join of this thread waits for.</summary>
            internal Resource Progress { get; }

            internal WaitRecord Wait => _wait;

            internal Frame Frames => _frames;

            internal void Push(WaitRecord record) => _wait = record;

            internal void Pop(WaitRecord record)
            {
                Interlocked.Increment(ref this.Version);
                if (ReferenceEquals(_wait, record)) _wait = record.Outer;
                Interlocked.Increment(ref this.Version);
            }

            internal void Enter(object owner, bool claimsAll) => _frames = new Frame(owner, claimsAll, _frames);

            /// <summary>Remove the innermost frame of <paramref name="owner"/>; null when there is none.</summary>
            internal Frame Exit(object owner)
            {
                var path = new List<Frame>();
                var frame = _frames;
                while (frame != null && !ReferenceEquals(frame.Owner, owner))
                {
                    path.Add(frame);
                    frame = frame.Outer;
                }
                if (frame == null) return null;
                var rebuilt = frame.Outer;
                for (var i = path.Count - 1; i >= 0; i--) rebuilt = new Frame(path[i].Owner, path[i].ClaimsAll, rebuilt);
                Interlocked.Increment(ref this.Version);
                _frames = rebuilt;
                Interlocked.Increment(ref this.Version);
                return frame;
            }

            internal bool Executes(object owner, bool claimsOnly)
            {
                for (var frame = _frames; frame != null; frame = frame.Outer)
                    if (ReferenceEquals(frame.Owner, owner) && (!claimsOnly || frame.ClaimsAll)) return true;
                return false;
            }

            /// <summary>Resources this thread holds, for lock-order tracking; any thread may end a hold.</summary>
            internal Resource[] Remember(Resource resource)
            {
                lock (_held)
                {
                    var before = new List<Resource>(_held.Count);
                    _held.RemoveAll(entry => !entry.TryGetTarget(out _));
                    foreach (var entry in _held)
                        if (entry.TryGetTarget(out var target)) before.Add(target);
                    _held.Add(new WeakReference<Resource>(resource));
                    return before.ToArray();
                }
            }

            internal void Forget(Resource resource)
            {
                lock (_held)
                {
                    var index = _held.FindIndex(entry => entry.TryGetTarget(out var target) && ReferenceEquals(target, resource));
                    if (index >= 0) _held.RemoveAt(index);
                }
            }

            public override string ToString() => _thread.TryGetTarget(out var thread)
                ? $"thread '{thread.Name ?? "unnamed"}' #{_managedId}"
                : $"thread (exited) #{_managedId}";
        }

        /// <summary>A registered wait; dispose it as soon as the blocking call returns.</summary>
        internal readonly struct WaitScope : IDisposable
        {
            private readonly ThreadState _state;
            private readonly WaitRecord _record;

            internal WaitScope(ThreadState state, WaitRecord record)
            {
                _state = state;
                _record = record;
            }

            public void Dispose() => _state?.Pop(_record);
        }
    }
}
#endif
