using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>One latched or evaluated ownership violation.</summary>
    internal sealed class OwnershipViolation
    {
        /// <summary>RELEASED_BEFORE_TEARDOWN or CORE_WITHOUT_PROTECTION.</summary>
        public string Kind { get; set; }
        public string Detail { get; set; }
        public override string ToString() => this.Kind + ": " + this.Detail;
    }

    /// <summary>
    /// The Shared ownership invariant, tracked per core and per ownership generation through the
    /// TESTING-only <see cref="SharedOwnershipEvents"/> (not through the connection's fields):
    /// <list type="bullet">
    /// <item>A core that requires writer exclusion (the connection's operation core, or a snapshot
    /// streaming under the mutex) and is active <b>or still tearing down</b> keeps its protection:
    /// the native writer mutex is held. Checked by <see cref="Evaluate"/>: a fresh probe thread
    /// takes the mutex with WaitOne(0) first and only then reads the core registry, so a core seen
    /// open or closing while the probe holds the mutex is a real violation, never a race.</item>
    /// <item>Protection released implies that core's teardown completed. Checked at every release
    /// of a connection's mutex (owner thread, scoped caller or pin holder): any protected core of
    /// that connection still opened or closing is latched as RELEASED_BEFORE_TEARDOWN.</item>
    /// </list>
    /// Holding the mutex without an attached core is valid (OpenDatabase acquires before it opens,
    /// close paths detach before they close), so it is never a violation. Violations are latched
    /// in a side channel and reported by the caller; nothing is thrown into the engine.
    /// </summary>
    internal sealed class OwnershipMonitor : IDisposable
    {
        private readonly ConcurrentDictionary<LiteEngine, Core> _cores = new ConcurrentDictionary<LiteEngine, Core>();
        private readonly ConcurrentQueue<OwnershipViolation> _latched = new ConcurrentQueue<OwnershipViolation>();
        private readonly Action<SharedEngine, LiteEngine, string> _stage;
        private readonly Action<Mutex> _releasing;

        private sealed class Core
        {
            public SharedEngine Connection;
            public Mutex Mutex;
            public string Stage;
            public int Generation;
            public int Id;
        }

        private int _ids;
        private readonly Func<bool> _accept;

        public OwnershipMonitor() : this(null)
        {
        }

        /// <param name="accept">
        /// Evaluated on the thread raising each event: only events it accepts are tracked, so monitors
        /// of scenarios running in parallel (for example one per teardown sweep case, recognised by the
        /// sweep scenario its threads carry) do not see each other's connections. Null accepts all.
        /// </param>
        public OwnershipMonitor(Func<bool> accept)
        {
            _accept = accept;
            _stage = this.OnStage;
            _releasing = this.OnReleasing;
            SharedOwnershipEvents.CoreStage += _stage;
            SharedOwnershipEvents.Releasing += _releasing;
        }

        /// <summary>Cores of <paramref name="connection"/> opened and not yet closed.</summary>
        public int LiveCores(SharedEngine connection) =>
            _cores.Values.Count(core => ReferenceEquals(core.Connection, connection) && core.Stage != SharedOwnershipEvents.Closed);

        public bool TryTake(out OwnershipViolation violation) => _latched.TryDequeue(out violation);

        public OwnershipViolation[] TakeAll()
        {
            var all = new List<OwnershipViolation>();
            while (_latched.TryDequeue(out var item)) all.Add(item);
            return all.ToArray();
        }

        /// <summary>Point-in-time check of "core active or tearing down implies protection held".</summary>
        public OwnershipViolation Evaluate(SharedEngine connection)
        {
            var mutex = connection.MutexOwner.Mutex;
            OwnershipViolation found = null;
            Exception error = null;
            var probe = new Thread(() =>
            {
                try
                {
                    bool free;
                    try { free = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { free = true; }
                    if (!free) return;
                    try
                    {
                        var live = this.Live(connection);
                        if (live.Length > 0)
                            found = new OwnershipViolation
                            {
                                Kind = "CORE_WITHOUT_PROTECTION",
                                Detail = "The writer mutex is free while protected core(s) are " + string.Join(", ", live)
                            };
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "safety ownership probe" };
            probe.Start();
            probe.Join();
            if (error != null) throw new InvalidOperationException("The ownership probe failed: " + error.Message, error);
            return found;
        }

        private void OnStage(SharedEngine connection, LiteEngine core, string stage)
        {
            if (_accept != null && !_accept()) return;
            if (stage == SharedOwnershipEvents.Opened)
            {
                _cores[core] = new Core
                {
                    Connection = connection, Mutex = connection.MutexOwner.Mutex, Stage = stage,
                    Generation = connection.MutexOwner.Generation, Id = Interlocked.Increment(ref _ids)
                };
            }
            else if (_cores.TryGetValue(core, out var tracked))
            {
                tracked.Stage = stage;
                // Closed cores are kept out of the registry once their teardown completed.
                if (stage == SharedOwnershipEvents.Closed) _cores.TryRemove(core, out _);
            }
        }

        private void OnReleasing(Mutex mutex)
        {
            if (_accept != null && !_accept()) return;
            var live = _cores.Values.Where(core => ReferenceEquals(core.Mutex, mutex) && core.Stage != SharedOwnershipEvents.Closed)
                .Select(Describe).ToArray();
            if (live.Length == 0) return;
            _latched.Enqueue(new OwnershipViolation
            {
                Kind = "RELEASED_BEFORE_TEARDOWN",
                Detail = $"Thread '{Thread.CurrentThread.Name}' ({Thread.CurrentThread.ManagedThreadId}) released the writer " +
                    "mutex while protected core(s) were " + string.Join(", ", live)
            });
        }

        private string[] Live(SharedEngine connection) => _cores.Values
            .Where(core => ReferenceEquals(core.Connection, connection) && core.Stage != SharedOwnershipEvents.Closed)
            .Select(Describe).ToArray();

        private static string Describe(Core core) => $"core#{core.Id} {core.Stage} (opened in ownership generation {core.Generation})";

        public void Dispose()
        {
            SharedOwnershipEvents.CoreStage -= _stage;
            SharedOwnershipEvents.Releasing -= _releasing;
        }
    }
}
