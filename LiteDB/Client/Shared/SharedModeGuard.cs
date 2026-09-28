using System;
using System.IO;
using System.Runtime.ConstrainedExecution;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>One independently disposable reference to the process's database lease.</summary>
    internal sealed class SharedModeGuard : CriticalFinalizerObject, IDisposable
    {
        private DatabaseAdmissionRegistry.Entry _entry;
        private readonly bool _engine;

        internal SharedModeGuard(DatabaseAdmissionRegistry.Entry entry, bool engine)
        {
            _entry = entry;
            _engine = engine;
        }

        internal static SharedModeGuard Open(EngineSettings settings)
        {
            if (!IsFile(settings) || settings.RebuildCandidate || settings.CoordinatedReadSnapshot) return null;
            var readOnly = settings.SharedMode ? settings.SharedModeReadOnly :
                settings.ReadOnly && !settings.Upgrade && !settings.AutoRebuild;
            try
            {
                return Open(settings.Filename, settings.SharedMode, settings.SharedMutexNameStrategy,
                    readOnly, engine: !settings.SharedMode && !readOnly, create: !settings.ReadOnly);
            }
            catch (IOException error) when (settings.ReadOnly && IsMissing(error))
            { throw MissingReadOnly(settings.Filename, error); }
        }

        internal static bool IsFile(EngineSettings settings) => settings.DataStream == null &&
            !string.IsNullOrEmpty(settings.Filename) && settings.Filename != ":memory:" && settings.Filename != ":temp:";

        internal static void Normalize(EngineSettings settings)
        {
            try
            {
                if (!IsFile(settings)) return;
                var original = Path.GetFullPath(settings.Filename);
                var canonical = DatabaseFileIdentity.CanonicalPath(original);
                if (!string.Equals(original, canonical, DatabaseFileIdentity.Windows
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    // Older executables could have selected sidecars beside a file
                    // symlink. Never abandon that WAL or bypass its recovery marker.
                    RebuildRecovery.EnsureAvailable(settings);
                    var aliasLog = FileHelper.GetLogFile(original);
                    if (FileHelper.ExistsOrThrow(aliasLog) && !string.Equals(
                        DatabaseFileIdentity.CanonicalPath(aliasLog),
                        DatabaseFileIdentity.CanonicalPath(FileHelper.GetLogFile(canonical)),
                        DatabaseFileIdentity.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new DatabaseAdmissionException(original, new IOException(
                            "The alias has a separate WAL. Recover the data and matching WAL together at one canonical path before opening."));
                }
                settings.Filename = canonical;
            }
            catch (IOException error) when (settings.ReadOnly && IsMissing(error))
            { throw MissingReadOnly(settings.Filename, error); }
        }

        internal static SharedModeGuard Open(string filename, bool shared, SharedMutexNameStrategy strategy,
            bool readOnly = false, bool engine = false, bool? create = null)
        {
            filename = DatabaseFileIdentity.CanonicalPath(filename);
            SharedCoordinationPolicy.RequireFileLocking();
            try
            {
                return SharedCoordinationFile.RetrySharingViolation(() =>
                    DatabaseAdmissionRegistry.Open(filename, shared, strategy, readOnly, engine, create ?? !readOnly));
            }
            catch (IOException error) when (!(error is DirectoryNotFoundException) &&
                !(error is FileNotFoundException) && !(error is PathTooLongException))
            {
                SharedCoordinationEvents.Log.Transition(filename, "mode-conflict", error.Message);
                throw new DatabaseAdmissionException(filename, error);
            }
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry != null) DatabaseAdmissionRegistry.Release(entry, _engine);
            GC.SuppressFinalize(this);
        }

        internal void EnsureValid()
        {
            lock (DatabaseAdmissionRegistry.Gate)
            {
                if (_entry == null) throw new ObjectDisposedException(nameof(SharedModeGuard));
                if (_entry.Faulted) throw new DatabaseAdmissionException(_entry.Filename,
                    new IOException("Native admission conversion failed. Close all local connections before retrying."));
            }
        }

        private static bool IsMissing(IOException error) => error is FileNotFoundException || error is DirectoryNotFoundException;

        private static LiteException MissingReadOnly(string filename, IOException error) =>
            new LiteException(LiteException.FILE_NOT_FOUND, error,
                "File '{0}' does not exist and cannot be created in read-only mode.", filename);

        // Critical finalization follows FileStream's ordinary finalizers, which
        // can still flush buffers. Never admit a peer before those writes finish.
        ~SharedModeGuard() { this.Dispose(); }
    }
}
