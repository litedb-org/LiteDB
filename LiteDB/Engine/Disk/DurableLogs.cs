using System;
using System.Collections.Generic;
using System.Linq;
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

        // Recording order: past the capacity the oldest path is forgotten, never one just proven. Each
        // entry carries the record it was queued for, so an entry a Forget and a later Record left behind
        // is skipped instead of evicting the newer record.
        private static readonly Queue<KeyValuePair<string, long>> _order = new Queue<KeyValuePair<string, long>>();
        private static readonly Dictionary<string, long> _records = new Dictionary<string, long>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private static long _next;

        internal static void Record(string path)
        {
            lock (_paths)
            {
                if (!_paths.Add(path)) return;
                var record = ++_next;
                _records[path] = record;
                _order.Enqueue(new KeyValuePair<string, long>(path, record));
                while (_order.Count > 0 && (_paths.Count > Capacity || !IsLive(_order.Peek())))
                {
                    var oldest = _order.Dequeue();
                    if (!IsLive(oldest)) continue;
                    _paths.Remove(oldest.Key);
                    _records.Remove(oldest.Key);
                }
                // Skipped entries behind a live front: drop them before they pile up.
                if (_order.Count > 4 * Capacity)
                {
                    var live = _order.Where(IsLive).ToList();
                    _order.Clear();
                    foreach (var entry in live) _order.Enqueue(entry);
                }
            }
        }

        private static bool IsLive(KeyValuePair<string, long> entry) =>
            _records.TryGetValue(entry.Key, out var record) && record == entry.Value;

        internal static bool Contains(string path)
        {
            lock (_paths) return _paths.Contains(path);
        }

        internal static void Forget(string path)
        {
            if (path == null) return;
            lock (_paths)
            {
                _paths.Remove(path);
                _records.Remove(path);
            }
        }
    }
}
