using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Cold-path admission only. No operation or cursor retains a reference.
    internal sealed class NativeAdmission : System.Runtime.ConstrainedExecution.CriticalFinalizerObject, IDisposable
    {
        internal static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private Entry _entry;

        internal sealed class Entry
        {
            internal DatabaseFileLock File;
            internal string Path;
            internal bool Shared, OneFile;
            internal volatile bool Faulted;
            internal int References;
        }

        private NativeAdmission(Entry entry) { _entry = entry; }
        internal string Identity => _entry.File.Identity;

        internal static bool IsFile(EngineSettings settings) => settings.DataStream == null &&
            settings.LogStream == null && settings.TempStream == null &&
            !string.IsNullOrEmpty(settings.Filename) && settings.Filename != ":memory:" && settings.Filename != ":temp:";

        internal static void Normalize(EngineSettings settings)
        {
            var original = System.IO.Path.GetFullPath(settings.Filename);
            var canonical = DatabaseFileIdentity.CanonicalPath(original);
            if (original != canonical)
            {
                var parentResolved = System.IO.Path.Combine(DatabaseFileIdentity.CanonicalPath(System.IO.Path.GetDirectoryName(original)),
                    System.IO.Path.GetFileName(original));
                var aliasLog = FileHelper.GetLogFile(original);
                if (parentResolved != canonical && FileHelper.ExistsOrThrow(aliasLog) &&
                    DatabaseFileIdentity.CanonicalPath(aliasLog) != DatabaseFileIdentity.CanonicalPath(FileHelper.GetLogFile(canonical)))
                    throw new IOException("Database alias has a separate WAL. Recover the matching data/WAL pair before opening.");
            }
            settings.Filename = canonical;
            RebuildRecovery.EnsureAvailable(settings);
        }

        internal static void NormalizeSharedNamespace(EngineSettings settings)
        {
            if (settings.Filename == ":memory:" || settings.Filename == ":temp:") return;
            settings.Filename = System.IO.Path.GetFullPath(settings.Filename);
            // Caller FileStreams use the same mutex namespace as physical-file peers,
            // including parents reached through a symlink (Darwin /var, for example).
            if (settings.DataStream is FileStream || settings.LogStream is FileStream)
                settings.Filename = DatabaseFileIdentity.CanonicalPath(settings.Filename);
        }

        internal static NativeAdmission Open(EngineSettings settings, bool shared, bool normalized = false, bool create = true)
        {
            if (!normalized) Normalize(settings);
            using var pathGate = shared ? EnterPath(settings.Filename) : null;
            lock (Gate)
            {
                RebuildRecovery.EnsureAvailable(settings);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) DatabaseVolumePolicy.RequireDatabaseMount(settings.Filename);
                DatabaseFileLock file = null;
                try
                {
                    // Unix exclusive OFD admission requires a writable descriptor, even
                    // for a read-only Direct engine. Windows LockFileEx needs read access.
                    try
                    {
                        // Explicit upgrade can replace v7 before the read-only
                        // connection opens; its admission must support exclusive OFD.
                        file = new DatabaseFileLock(settings.Filename, shared && settings.ReadOnly && !settings.Upgrade, false, settings.Filename);
                    }
                    catch (FileNotFoundException) when (!settings.ReadOnly && create)
                    {
                        RebuildRecovery.EnsureAvailable(settings);
                        file = new DatabaseFileLock(settings.Filename, false, true, settings.Filename);
                    }
                    if (Entries.TryGetValue(file.Identity, out var entry))
                    {
                        if (entry.Shared != shared || (shared && entry.OneFile != settings.SharedOneFile) || !string.Equals(entry.Path, settings.Filename,
                            DatabaseFileIdentity.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new IOException("Incompatible local Direct/Shared access or database alias.");
                        if (entry.Faulted) throw new IOException("Native replacement failed. Close all local connections before reopening.");
                        var reference = new NativeAdmission(entry);
                        entry.References++;
                        return reference;
                    }
                    file.Lock(DatabaseFileLock.Admission, exclusive: !shared);
                    RebuildRecovery.EnsureAvailable(settings);
                    if (shared)
                    {
                        DatabasePathLock.Claim(file, settings.Filename);
                        ClaimFamily(file, settings.SharedOneFile);
                    }
                    using (var probe = new DatabaseFileLock(settings.Filename, true, false, settings.Filename, qualify: false))
                        if (probe.Identity != file.Identity || !probe.Conflicts(DatabaseFileLock.Admission))
                            throw new IOException("Filesystem did not enforce native database admission.");
                    entry = new Entry { File = file, Path = settings.Filename, Shared = shared, OneFile = settings.SharedOneFile, References = 1 };
                    Entries.Add(file.Identity, entry);
                    file = null;
                    return new NativeAdmission(entry);
                }
                catch (IOException ex) when (!(ex is FileNotFoundException) && !(ex is DirectoryNotFoundException))
                {
                    throw new IOException("Database admission refused: another process owns incompatible Direct/Shared access, or native locking failed. " + ex.Message, ex);
                }
                finally { file?.Dispose(); }
            }
        }

        private static void ClaimFamily(DatabaseFileLock file, bool oneFile)
        {
            // Claim before probing: unlike concurrent claimants cannot both pass.
            file.Lock(DatabaseFileLock.Family + (oneFile ? 1 : 0), false);
            if (file.Conflicts(DatabaseFileLock.Family + (oneFile ? 0 : 1)))
                throw new IOException("Incompatible Shared coordination ABI family; stop all participants before switching.");
        }

        internal NativeAdmission Retain()
        {
            lock (Gate)
            {
                this.EnsureValid();
                var reference = new NativeAdmission(_entry);
                _entry.References++;
                return reference;
            }
        }

        internal void EnsureValid()
        {
            var entry = _entry;
            if (entry == null) throw new ObjectDisposedException(nameof(NativeAdmission));
            if (entry.Faulted) throw new IOException("Native replacement failed. Close all local connections before reopening.");
        }

        ~NativeAdmission() { this.Dispose(); }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry == null) return;
            lock (Gate)
            {
                if (--entry.References != 0) return;
                Entries.Remove(entry.File.Identity);
                entry.File.Dispose();
            }
        }

        internal static IDisposable EnterPath(string filename) =>
            new PathMutex(DatabaseFileIdentity.CanonicalPath(filename));

        private sealed class PathMutex : IDisposable
        {
            private readonly Mutex _mutex;
            internal PathMutex(string filename)
            {
                _mutex = SharedMutexFactory.Create("LiteDB.Admission.Path." + SharedMutexNameFactory.CreateUsingSha1(filename));
                try
                {
                    try { if (!_mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Database admission is busy; retry opening."); }
                    catch (AbandonedMutexException) { }
                }
                catch { _mutex.Dispose(); throw; }
            }
            public void Dispose() { try { _mutex.ReleaseMutex(); } finally { _mutex.Dispose(); } }
        }

        // Called before the recovery marker permits any rename. The path mutex
        // fences openers across the missing-name interval, and both inodes are locked.
        internal static Replacement LockReplacement(EngineSettings settings, string candidate)
        {
            var path = EnterPath(settings.Filename);
            DatabaseFileLock replacement = null;
            Entry upgraded = null;
            try
            {
                lock (Gate)
                {
                    using var identity = new DatabaseFileLock(settings.Filename, true, false);
                    if (!Entries.TryGetValue(identity.Identity, out var entry))
                        return new Replacement(path, null, null);
                    if (entry.Shared)
                    {
                        if (DatabaseFileIdentity.Windows) entry.File.Unlock(DatabaseFileLock.Admission);
                        try { entry.File.Lock(DatabaseFileLock.Admission, true); }
                        catch
                        {
                            if (DatabaseFileIdentity.Windows) entry.File.Lock(DatabaseFileLock.Admission, false);
                            throw;
                        }
                    }
                    if (entry.Shared) upgraded = entry;
                    replacement = new DatabaseFileLock(candidate, false, false);
                    replacement.Lock(DatabaseFileLock.Admission, true);
                    var result = new Replacement(path, entry, replacement);
                    replacement = null;
                    upgraded = null;
                    return result;
                }
            }
            catch
            {
                try
                {
                    replacement?.Dispose();
                    upgraded?.File.Downgrade(DatabaseFileLock.Admission);
                }
                finally { path.Dispose(); }
                throw;
            }
        }

        internal sealed class Replacement : IDisposable
        {
            private readonly IDisposable _path;
            private readonly Entry _entry;
            private readonly DatabaseFileLock _replacement;
            internal Exception PrimaryFailure;
            internal bool Published;
            internal Replacement(IDisposable path, Entry entry, DatabaseFileLock replacement)
            { _path = path; _entry = entry; _replacement = replacement; }
            public void Dispose()
            {
                try
                {
                    if (_entry == null) return;
                    lock (Gate)
                    {
                        if (FileHelper.ExistsOrThrow(_entry.Path))
                        {
                            using var live = new DatabaseFileLock(_entry.Path, true, false);
                            if (live.Identity == _replacement.Identity)
                            {
                                Entries.Remove(_entry.File.Identity);
                                _entry.File.Dispose();
                                _entry.File = _replacement;
                                Entries.Add(_replacement.Identity, _entry);
                                if (_entry.Shared)
                                {
                                    DatabasePathLock.Claim(_replacement, _entry.Path);
                                    ClaimFamily(_replacement, _entry.OneFile);
                                    _replacement.Downgrade(DatabaseFileLock.Admission);
                                }
                                return;
                            }
                        }
                        _replacement.Dispose();
                        if (_entry.Shared) _entry.File.Downgrade(DatabaseFileLock.Admission);
                    }
                }
                catch (Exception error)
                {
                    if (_entry != null) _entry.Faulted = true;
                    if (PrimaryFailure != null)
                    {
                        var previous = PrimaryFailure.Data[RebuildService.RollbackErrorsDataKey] as AggregateException;
                        PrimaryFailure.Data[RebuildService.RollbackErrorsDataKey] = previous == null
                            ? new AggregateException(error) : new AggregateException(previous, error);
                        return;
                    }
                    if (Published) error.Data[RebuildService.LiveStateDataKey] = RebuildService.LiveStateReplacement;
                    throw;
                }
                finally { _path.Dispose(); }
            }
        }
    }
}
