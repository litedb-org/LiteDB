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
            internal SharedMemoryReaderTable Table;
            internal Thread Initializer;
            internal volatile bool Retired;
        }

        internal static SharedMemoryReaders Attach(EngineSettings settings)
        {
            using var admission = NativeAdmission.Open(settings, true);
            // Native admission validates physical identity and rejects hard-link/namespace
            // aliases. The canonical path stays stable across an exclusive replacement.
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
                        table = SharedMemoryReaderTable.Open(settings);
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
                DirectHostSettings.RequireCompatible(captured, opening, captured.Collation.ToString());
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
