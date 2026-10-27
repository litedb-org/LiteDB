using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Process-local visibility for legacy, thread-bound Shared transactions. The
    /// native mutex still orders storage access; this registry only prevents a
    /// same-thread peer write from waiting on an owner that only that thread can
    /// complete.
    /// </summary>
    internal static class SharedTransactionOwnership
    {
        internal sealed class Entry
        {
            internal readonly string Namespace;
            internal readonly object Facade;
            internal readonly Thread Thread;
            internal readonly TimeSpan Timeout;

            internal Entry(string ns, object facade, Thread thread, TimeSpan timeout)
            {
                Namespace = ns;
                Facade = facade;
                Thread = thread;
                Timeout = timeout;
            }
        }

        internal sealed class Lease : IDisposable
        {
            private readonly Entry _entry;
            private int _released;

            internal Lease(Entry entry) => _entry = entry;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                lock (Gate)
                {
                    if (Entries.TryGetValue(_entry.Namespace, out var current) && ReferenceEquals(current, _entry))
                    {
                        Entries.Remove(_entry.Namespace);
                        Monitor.PulseAll(Gate);
                    }
                }
            }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static Lease Enter(string ns, object facade, Thread thread, TimeSpan timeout)
        {
            lock (Gate)
            {
                RemoveDeadOwnerLocked(ns);
                if (Entries.ContainsKey(ns))
                    throw LiteDB.LiteException.SameThreadTransaction();

                var entry = new Entry(ns, facade, thread, timeout);
                Entries.Add(ns, entry);
                return new Lease(entry);
            }
        }

        /// <summary>
        /// Refuse immediately by default. When enabled, wait for the foreign
        /// explicit transaction to end, bounded by the owner's TIMEOUT pragma.
        /// </summary>
        internal static void WaitForWrite(string ns, object facade, Thread thread, bool wait)
        {
            lock (Gate)
            {
                var started = Stopwatch.StartNew();
                while (true)
                {
                    RemoveDeadOwnerLocked(ns);
                    if (!Entries.TryGetValue(ns, out var entry) || ReferenceEquals(entry.Facade, facade) ||
                        !ReferenceEquals(entry.Thread, thread)) return;

                    if (!wait) throw LiteDB.LiteException.SameThreadTransaction();

                    var remaining = entry.Timeout - started.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        throw LiteDB.LiteException.LockTimeout("same-thread transaction", entry.Timeout);
                    Monitor.Wait(Gate, remaining);
                }
            }
        }

        private static void RemoveDeadOwnerLocked(string ns)
        {
            if (!Entries.TryGetValue(ns, out var entry) || entry.Thread.IsAlive) return;
            Entries.Remove(ns);
            Monitor.PulseAll(Gate);
        }
    }
}
