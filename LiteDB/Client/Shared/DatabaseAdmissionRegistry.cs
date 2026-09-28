using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    internal static class DatabaseAdmissionRegistry
    {
        internal static readonly object Gate = new object();
        internal static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        internal sealed class Entry
        {
            internal readonly List<DatabaseFileLock> Files = new List<DatabaseFileLock>();
            internal DatabaseFileLock Current;
            internal string Filename;
            internal FileStream Legacy;
            internal int Family, References;
            internal bool Engine, Replacing, Faulted;
        }

        internal static SharedModeGuard Open(string filename, bool shared, SharedMutexNameStrategy strategy,
            bool readOnly, bool engine, bool create)
        {
            RebuildRecovery.EnsureAvailable(new EngineSettings { Filename = filename });
            SharedCoordinationFile.Observe(filename, "mode-before-path-lock");
            using var pathGate = EnterPath(filename);
            RebuildRecovery.EnsureAvailable(new EngineSettings { Filename = filename });
            var family = shared ? (strategy == SharedMutexNameStrategy.Sha1Hash ? 1 : 0) : readOnly ? 2 : -1;
            lock (Gate)
            {
                var file = new DatabaseFileLock(filename, readOnly, create);
                try
                {
                    using var gate = Enter(file.Identity);
                    // A waiter may have opened the old inode before replacement.
                    using (var check = new DatabaseFileLock(filename, readOnly: true, create: false))
                        if (check.Identity != file.Identity) throw new IOException("Database changed during admission; retry opening.");
                    RebuildRecovery.EnsureAvailable(new EngineSettings { Filename = filename });
                    if (Entries.TryGetValue(file.Identity, out var entry))
                    {
                        if (entry.Family != family || entry.Replacing || entry.Faulted || (engine && entry.Engine))
                            throw new IOException("Incompatible local database access. Independent Direct writers require one shared LiteEngine.");
                        return Retain(entry, engine);
                    }

                    SharedCoordinationFile.Observe(filename, "mode-locking");
                    file.Lock(DatabaseFileLock.Admission, exclusive: family < 0);
                    if (family >= 0)
                    {
                        for (var i = 0; i < 3; i++)
                            if (i != family && file.Conflicts(DatabaseFileLock.Family + i))
                                throw new IOException("Incompatible Shared mutex identity or Direct read-only participant.");
                        file.Lock(DatabaseFileLock.Family + family, exclusive: false);
                    }
                    // Detect unsupported/no-op locking before storage can write.
                    using (var probe = new DatabaseFileLock(filename, readOnly: true, create: false))
                        if (!probe.Conflicts(DatabaseFileLock.Admission))
                            throw new IOException("The filesystem did not enforce the database admission lock.");
                    SharedCoordinationFile.Observe(filename, "mode-locked");
                    entry = new Entry { Current = file, Filename = filename, Family = family };
                    if (family < 0) entry.Legacy = OpenLegacy(filename);
                    entry.Files.Add(file);
                    Entries.Add(file.Identity, entry);
                    file = null;
                    return Retain(entry, engine);
                }
                finally { file?.Dispose(); }
            }
        }

        private static SharedModeGuard Retain(Entry entry, bool engine)
        {
            entry.References++;
            if (engine) entry.Engine = true;
            try
            {
                SharedCoordinationFile.Observe(entry.Filename, "mode-retaining");
                return new SharedModeGuard(entry, engine);
            }
            catch
            {
                // Reference-object allocation or publication can fail after native
                // admission. The registry must not root an unreturnable reference.
                Release(entry, engine);
                throw;
            }
        }

        private static FileStream OpenLegacy(string filename)
        {
            if (!SharedCoordinationFallback.SupportsNames(filename)) return null;
            // Older mapped participants still advertise their lifetime here.
            try { return new FileStream(SharedCoordinationFallback.LivePath(filename), FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (FileNotFoundException)
            {
                var authority = SharedCoordinationFallback.PagePath(filename);
                if (FileHelper.ExistsOrThrow(authority))
                    throw new IOException("Unpaired Shared coordination authority: '" + authority +
                        "'. Stop all connections to this database, then remove this orphan coordination file " +
                        "before retrying. Preserve database, WAL, backup and rebuild-recovery files.");
                return null;
            }
        }

        internal static void Release(Entry entry, bool engine)
        {
            lock (Gate)
            {
                if (engine) entry.Engine = false;
                if (--entry.References != 0) return;
                try { entry.Legacy?.Dispose(); }
                finally
                {
                    foreach (var file in entry.Files)
                    {
                        Entries.Remove(file.Identity);
                        file.Dispose();
                    }
                }
            }
        }

        // A short, thread-scoped bootstrap mutex serializes compatibility probes
        // and lock conversion. It is never the connection's lifetime lease.
        internal static IDisposable Enter(string identity) => new AdmissionMutex(identity);
        internal static IDisposable EnterPath(string filename) =>
            Enter("Path." + SharedMutexNameFactory.CreateUsingSha1(filename));

        private sealed class AdmissionMutex : IDisposable
        {
            private readonly Mutex _mutex;
            internal AdmissionMutex(string identity)
            {
                _mutex = SharedMutexFactory.Create("LiteDB.Admission." + identity);
                try
                {
                    try
                    {
                        if (!_mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Database admission is busy; retry opening.");
                    }
                    catch (AbandonedMutexException) { }
                }
                catch { _mutex.Dispose(); throw; }
            }
            public void Dispose()
            {
                try { _mutex.ReleaseMutex(); }
                finally { _mutex.Dispose(); }
            }
        }
    }
}
