using System;
using System.IO;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Lock both file identities before the recovery marker permits renames. A
    /// Shared replacement requires all other processes to close their admission:
    /// their idle handles cannot be transferred to a new inode remotely.
    /// </summary>
    internal sealed class DatabaseReplacementLease : IDisposable
    {
        private readonly SharedModeGuard _reference;
        private readonly DatabaseAdmissionRegistry.Entry _entry;
        private readonly DatabaseFileLock _source, _replacement;
        private readonly string _filename;
        private readonly IDisposable _pathGate;
        internal Exception PrimaryFailure { get; set; }
        internal bool Published { get; set; }

        private DatabaseReplacementLease(SharedModeGuard reference, DatabaseAdmissionRegistry.Entry entry,
            DatabaseFileLock replacement, string filename, IDisposable pathGate)
        {
            _reference = reference;
            _entry = entry;
            _source = entry.Current;
            _replacement = replacement;
            _filename = filename;
            _pathGate = pathGate;
        }

        internal static DatabaseReplacementLease Begin(EngineSettings settings, string candidate)
        {
            var pathGate = DatabaseAdmissionRegistry.EnterPath(settings.Filename);
            SharedModeGuard reference = null;
            DatabaseFileLock replacement = null;
            try
            {
                reference = SharedModeGuard.Open(settings.Filename, settings.SharedMode,
                    settings.SharedMutexNameStrategy);
                lock (DatabaseAdmissionRegistry.Gate)
                {
                    using var identity = new DatabaseFileLock(settings.Filename, readOnly: true, create: false);
                    var entry = DatabaseAdmissionRegistry.Entries[identity.Identity];
                    using var gate = DatabaseAdmissionRegistry.Enter(identity.Identity);
                    if (entry.Replacing) throw new IOException("Database replacement is already in progress.");
                    replacement = new DatabaseFileLock(candidate, readOnly: false, create: false);
                    if (DatabaseAdmissionRegistry.Entries.ContainsKey(replacement.Identity))
                        throw new IOException("The rebuild candidate already has an admitted user.");
                    // Candidate is private, but acquire its lock before publishing it.
                    replacement.Lock(DatabaseFileLock.Admission, exclusive: true);
                    if (entry.Family >= 0)
                    {
                        replacement.Lock(DatabaseFileLock.Family + entry.Family, exclusive: false);
                        Upgrade(entry);
                    }
                    var lease = new DatabaseReplacementLease(reference, entry, replacement, settings.Filename, pathGate);
                    entry.Replacing = true;
                    entry.Files.Add(replacement);
                    DatabaseAdmissionRegistry.Entries.Add(replacement.Identity, entry);
                    replacement = null;
                    return lease;
                }
            }
            catch
            {
                replacement?.Dispose();
                reference?.Dispose();
                pathGate.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                lock (DatabaseAdmissionRegistry.Gate)
                {
                    // Rollback can leave either complete pair live, or a guarded
                    // incomplete installation. Local references follow the entry;
                    // detach the unused inode so backups/candidates stay recoverable.
                    if (FileHelper.ExistsOrThrow(_filename))
                    {
                        using var live = new DatabaseFileLock(_filename, readOnly: true, create: false);
                        if (live.Identity == _replacement.Identity) _entry.Current = _replacement;
                    }
                    if (_entry.Family >= 0)
                    {
                        Downgrade(_entry.Current);
                    }
                    var retired = ReferenceEquals(_entry.Current, _source) ? _replacement : _source;
                    DatabaseAdmissionRegistry.Entries.Remove(retired.Identity);
                    _entry.Files.Remove(retired);
                    retired.Dispose();
                    _entry.Replacing = false;
                }
            }
            catch (Exception error)
            {
                lock (DatabaseAdmissionRegistry.Gate) _entry.Faulted = true;
                if (PrimaryFailure != null)
                {
                    var previous = PrimaryFailure.Data[RebuildService.RollbackErrorsDataKey] as AggregateException;
                    PrimaryFailure.Data[RebuildService.RollbackErrorsDataKey] = previous == null
                        ? new AggregateException(error) : new AggregateException(previous, error);
                    return;
                }
                // Installation succeeded before native cleanup failed. Let the
                // caller retain the replacement's password/collation on failure.
                if (Published) error.Data[RebuildService.LiveStateDataKey] = RebuildService.LiveStateReplacement;
                throw;
            }
            finally
            {
                try { _reference.Dispose(); }
                finally { _pathGate.Dispose(); }
            }
        }

        private static void Downgrade(DatabaseFileLock file)
        {
            using var gate = DatabaseAdmissionRegistry.Enter(file.Identity);
            file.Downgrade(DatabaseFileLock.Admission);
        }

        private static void Upgrade(DatabaseAdmissionRegistry.Entry entry)
        {
            var old = entry.Current;
            var writable = old;
            var released = false;
            try
            {
                // A read-only Shared connection may have acquired the first local
                // lease. OFD write locks need a writable fd, even for this conversion.
                if (old.ReadOnly && !DatabaseFileIdentity.Windows)
                {
                    writable = new DatabaseFileLock(old.Path, readOnly: false, create: false);
                    if (writable.Identity != old.Identity) throw new IOException("Database changed before replacement.");
                    writable.Lock(DatabaseFileLock.Family + entry.Family, exclusive: false);
                }
                if (DatabaseFileIdentity.Windows || !ReferenceEquals(old, writable))
                {
                    old.Unlock(DatabaseFileLock.Admission);
                    released = true;
                }
                try { writable.Lock(DatabaseFileLock.Admission, exclusive: true); }
                catch (Exception error)
                {
                    if (released)
                    {
                        try { old.Lock(DatabaseFileLock.Admission, exclusive: false); }
                        catch { entry.Faulted = true; throw; }
                    }
                    throw new IOException("Close other processes' database connections before rebuilding or upgrading.", error);
                }
                if (!ReferenceEquals(old, writable))
                {
                    entry.Files.Remove(old);
                    entry.Files.Add(writable);
                    entry.Current = writable;
                    old.Dispose();
                }
            }
            finally
            {
                if (!ReferenceEquals(writable, entry.Current)) writable.Dispose();
            }
        }
    }
}
