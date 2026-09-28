using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiteDB.Engine
{
    /// <summary>
    /// Log files (by full path) whose storage synced in this process, file and directory (decision 3 of
    /// docs/decisions/durability-policy.md): later engines over the same log skip the proof before their
    /// first commit. A log that answers "cannot sync" later is forgotten, and each commit's own sync still
    /// runs; a replaced or remounted log that stops syncing fails that commit (its outcome unknown).
    /// </summary>
    internal static class DurableLogs
    {
        private const int Capacity = 1024;

        private static readonly HashSet<string> _paths = new HashSet<string>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Recording order: past the capacity the oldest path is forgotten, never one just proven.
        private static readonly Queue<string> _order = new Queue<string>();

        internal static void Record(string path)
        {
            lock (_paths)
            {
                if (!_paths.Add(path)) return;
                _order.Enqueue(path);
                // Forgotten paths stay queued until they reach the front; skip them there.
                while (_paths.Count > Capacity || (_order.Count > 0 && !_paths.Contains(_order.Peek())))
                    _paths.Remove(_order.Dequeue());
                if (_order.Count > 4 * Capacity) Compact();
            }
        }

        // Forget and re-record can leave stale queue entries behind the front: drop them.
        private static void Compact()
        {
            var live = new List<string>(_order);
            _order.Clear();
            var seen = new HashSet<string>(_paths.Comparer);
            foreach (var path in live)
                if (_paths.Contains(path) && seen.Add(path)) _order.Enqueue(path);
        }

        internal static bool Contains(string path)
        {
            lock (_paths) return _paths.Contains(path);
        }

        internal static void Forget(string path)
        {
            if (path == null) return;
            lock (_paths) _paths.Remove(path);
        }
    }
}
