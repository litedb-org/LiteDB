using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB.Client.Shared
{
    [EventSource(Name = "LiteDB-Shared")]
    internal sealed class SharedCoordinationEvents : EventSource
    {
        internal static readonly SharedCoordinationEvents Log = new SharedCoordinationEvents();
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        [Event(1, Level = EventLevel.Informational)]
        public void Transition(string filename, string state, string reason)
        {
            // Diagnostic subscribers must never change cleanup or admission outcomes.
            try { if (IsEnabled()) WriteEvent(1, filename, state, reason ?? ""); }
            catch (Exception) { }
        }

        [NonEvent]
        internal static void Attached(string filename)
        {
            var key = Path.GetFullPath(filename);
            lock (Counts)
            {
                Counts.TryGetValue(key, out var count);
                Counts[key] = count + 1;
            }
            Log.Transition(filename, "attached", "");
        }

        [NonEvent]
        internal static void Detached(string filename)
        {
            var key = Path.GetFullPath(filename);
            lock (Counts)
            {
                if (Counts.TryGetValue(key, out var count) && count > 1) Counts[key] = count - 1;
                else Counts.Remove(key);
            }
            Log.Transition(filename, "detached", "");
        }

        [NonEvent]
        internal static int Participants(string filename)
        {
            lock (Counts) return Counts.TryGetValue(Path.GetFullPath(filename), out var count) ? count : 0;
        }
    }
}
