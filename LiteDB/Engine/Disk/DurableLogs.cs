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

        internal static void Record(string path)
        {
            lock (_paths)
            {
                if (_paths.Count >= Capacity) _paths.Clear();
                _paths.Add(path);
            }
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
