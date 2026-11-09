#if NET8_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using LiteDB.Client.Direct;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Only open/final lifetime edges visit the registry. Slow initialization is
    // serialized per identity, never while holding a global dictionary monitor.
    internal static class SharedParticipantRegistry
    {
        private static readonly ConcurrentDictionary<string, Entry> Entries = new ConcurrentDictionary<string, Entry>();
        private sealed class Entry
        {
            internal readonly object Gate = new object();
            internal volatile SharedMemoryReaderTable Table;
            internal Thread Initializer;
            internal volatile bool Retired;
        }

        internal static SharedMemoryReaders Attach(EngineSettings settings)
        {
            // SharedEngine already holds validated native admission through this
            // entire attach. Retain that owner instead of reopening/qualifying
            // the same file twice inside one protected construction.
            using var admission = settings.SharedAdmission?.Retain() ?? NativeAdmission.Open(settings, true);
            // Keep the stable canonical namespace key across exclusive replacement.
            var identity = settings.Filename;
            while (true)
            {
                var entry = Entries.GetOrAdd(identity, _ => new Entry());
                lock (entry.Gate)
                {
                    if (entry.Retired) continue;
                    if (entry.Initializer == Thread.CurrentThread)
                        throw new InvalidOperationException("Recursive Shared participant initialization is unsupported.");
                    var table = entry.Table;
                    if (table != null)
                    {
                        if (table.Idle)
                        {
                            try
                            {
                                RequireCompatible(table, settings);
                                // A remote writer can die while we linger. A writable
                                // opener must independently recover an uncertain image;
                                // retaining the old bootstrap cannot certify that repair.
                                if (!settings.ReadOnly && !table.Settings.ReadOnly && !table.Reads.TryRead(out _) && table.ReleaseIdle())
                                    continue;
                            }
                            catch (InvalidOperationException)
                            {
                                if (table.ReleaseIdle()) continue;
                            }
                        }
                        if (!table.TryRetain()) { Retire(identity, entry); continue; }
                        try
                        {
                            RequireCompatible(table, settings);
                            return new SharedMemoryReaders(table);
                        }
                        catch { table.Release(); throw; }
                    }
                    entry.Initializer = Thread.CurrentThread;
                    try
                    {
                        table = SharedMemoryReaderTable.Open(settings, admission);
                        table.ClosedCallback = () => Retire(identity, entry);
                        entry.Table = table;
                        try { return new SharedMemoryReaders(table); }
                        catch { table.Release(); throw; }
                    }
                    catch { Retire(identity, entry); throw; }
                    finally { entry.Initializer = null; }
                }
            }
        }

        static SharedParticipantRegistry()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(SharedParticipantRegistry).Assembly).Unloading += _ =>
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                ReleaseOnExit();
            };
        }

        private static void OnProcessExit(object sender, EventArgs args) => ReleaseOnExit();

        private static void ReleaseOnExit()
        {
            foreach (var entry in Entries.Values)
            {
                try { entry.Table?.ReleaseIdle(); }
                catch (Exception) { /* Exit is best effort; OS locks die with the process. */ }
            }
        }

        internal static bool ReleaseIdle(string filename)
        {
            var identity = DatabaseFileIdentity.CanonicalPath(System.IO.Path.GetFullPath(filename));
            return Entries.TryGetValue(identity, out var entry) && entry.Table?.ReleaseIdle() == true;
        }

        internal static void ReleaseAllIdle()
        {
            foreach (var entry in Entries.Values) entry.Table?.ReleaseIdle();
        }

        // Test cleanup uses registered database names rather than enumerating
        // sidecars that final-close may delete, or following malformed aliases.
#if DEBUG || TESTING
        internal static void ReleaseIdleInDirectory(string directory)
        {
            var prefix = DatabaseFileIdentity.CanonicalPath(System.IO.Path.GetFullPath(directory))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var comparison = DatabaseFileIdentity.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var pair in Entries)
                if (pair.Key.StartsWith(prefix, comparison)) pair.Value.Table?.ReleaseIdle();
        }
#endif

        private static void Retire(string identity, Entry entry)
        {
            entry.Retired = true;
            // Conditional removal cannot erase a successor entry after close/retry.
            ((ICollection<KeyValuePair<string, Entry>>)Entries).Remove(new KeyValuePair<string, Entry>(identity, entry));
        }

        private static void RequireCompatible(SharedMemoryReaderTable table, EngineSettings requested)
        {
            var captured = table.Settings;
            try
            {
                Func<string, BsonValue, BsonValue> transform = null;
                if (table.HasReadTransform && !table.ReadTransform.TryGetTarget(out transform) ||
                    !ReferenceEquals(transform, requested.ReadTransform))
                    throw new InvalidOperationException("Shared read transforms differ.");
                var opening = requested.Clone();
                opening.ReadTransform = null;
                // Access is enforced by each facade's own core/snapshot settings.
                // The table owns coordination, not a shared writable storage core.
                // A read-only first table conservatively omits final-close checkpoint.
                if (!captured.SharedOneFileOverride.HasValue && !requested.SharedOneFileOverride.HasValue)
                    opening.ReadOnly = captured.ReadOnly;
                DirectHostSettings.RequireCompatible(captured, opening, captured.Collation.ToString());
                if (captured.SharedLingerMilliseconds != requested.SharedLingerMilliseconds)
                    throw new InvalidOperationException("Shared linger settings differ.");
                if (captured.SharedMutexNameStrategy != requested.SharedMutexNameStrategy ||
                    !ReferenceEquals(captured.SharedReaderFiles, requested.SharedReaderFiles))
                    throw new InvalidOperationException("Shared opening settings differ.");
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException("Incompatible Shared engine settings for an already-open physical database. Close all facades and results before changing settings.", error);
            }
            // Capacity/size requests deliberately do not participate: live header
            // declarations win for every later opener, including local facades.
        }
    }
}
#endif
