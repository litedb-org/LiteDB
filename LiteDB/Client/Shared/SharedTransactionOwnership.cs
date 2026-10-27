using System;
using System.Collections.Generic;
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

            internal Entry(string ns, object facade, Thread thread)
            {
                Namespace = ns;
                Facade = facade;
                Thread = thread;
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

        internal static Lease Enter(string ns, object facade, Thread thread)
        {
            lock (Gate)
            {
                RemoveDeadOwnerLocked(ns);
                if (Entries.ContainsKey(ns))
                    throw LiteDB.LiteException.SameThreadTransaction();

                var entry = new Entry(ns, facade, thread);
                Entries.Add(ns, entry);
                return new Lease(entry);
            }
        }

        internal static void EnsureWriteAllowed(string ns, object facade, Thread thread)
        {
            lock (Gate)
            {
                RemoveDeadOwnerLocked(ns);
                if (Entries.TryGetValue(ns, out var entry) && !ReferenceEquals(entry.Facade, facade) &&
                    ReferenceEquals(entry.Thread, thread))
                    throw LiteDB.LiteException.SameThreadTransaction();
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
