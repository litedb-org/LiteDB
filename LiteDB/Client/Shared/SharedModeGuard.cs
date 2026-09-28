using System;
using System.IO;
using System.Text;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// OS-backed admission for modern file connections. The file is never unlinked:
    /// replacing it would let two inodes authorize conflicting owners. Its contents
    /// are only a mutex identity, never database or recovery state.
    /// </summary>
    internal sealed class SharedModeGuard : IDisposable
    {
        private const string IdentityMagic = "LiteDB mode admission 1\n";
        private static readonly byte[] IdentityPrefix = Encoding.UTF8.GetBytes(IdentityMagic);
        private readonly FileStream _lease;
        private FileStream _legacyLease;
        private SharedModeGuard(FileStream lease) { _lease = lease; }

        internal static SharedModeGuard Open(EngineSettings settings)
        {
            if (settings.RebuildCandidate || settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) ||
                settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;
            var shared = settings.SharedMode;
            var readOnly = shared ? settings.SharedModeReadOnly : settings.ReadOnly && !settings.Upgrade && !settings.AutoRebuild;
            if (readOnly && !shared) return null;
            return SharedCoordinationFile.RetrySharingViolation(() =>
                Open(settings.Filename, shared, settings.SharedMutexNameStrategy, readOnly));
        }

        internal static SharedModeGuard Open(string filename, bool shared, SharedMutexNameStrategy strategy,
            bool readOnly = false)
        {
            filename = Path.GetFullPath(filename);
            if (!SharedCoordinationFallback.SupportsNames(filename)) return null;
            SharedCoordinationPolicy.RequireFileLocking();
            var path = filename + "-shared-mode";
            FileStream lease = null;
            var present = false;
            try
            {
                if (shared)
                {
                    var identity = Encoding.UTF8.GetBytes(IdentityMagic + SharedMutexNameFactory.Create(filename, strategy));
                    try
                    {
                        lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        present = true;
                        if (Matches(lease, identity)) return new SharedModeGuard(lease);
                        lease.Dispose();
                        lease = null;
                    }
                    catch (FileNotFoundException) { }
                    // Only exclusive ownership can initialize/change an idle identity.
                    // A crash here leaves no live owner; the next exclusive opener retries.
                    if (readOnly)
                    {
                        if (present)
                            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        return null;
                    }
                    using (var initialize = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Never truncate an unrelated file in our reserved namespace.
                        // Empty and prefix-only identities can be left by an interrupted
                        // initialization; complete recognized identities are ephemeral.
                        if (!CanInitialize(initialize)) throw new IOException("Unrecognized Shared mode admission identity: " + path);
                        SharedCoordinationFile.Observe(path, "mode-initializing");
                        initialize.SetLength(0);
                        initialize.Position = 0;
                        SharedCoordinationFile.Observe(path, "mode-truncated");
                        initialize.Write(identity, 0, identity.Length);
                        SharedCoordinationFile.Observe(path, "mode-written");
                        initialize.Flush();
                        SharedCoordinationFile.Observe(path, "mode-flushed");
                    }
                    lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!Matches(lease, identity)) throw new IOException("Conflicting Shared mutex identity.");
                }
                else lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
                var guard = new SharedModeGuard(lease);
                if (!shared)
                {
                    // Pre-guard mapped implementations still hold the participation
                    // file. Keep it exclusive so they cannot attach during this writer.
                    var live = SharedCoordinationFallback.LivePath(filename);
                    try { guard._legacyLease = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.None); }
                    catch (FileNotFoundException)
                    {
                        var authority = SharedCoordinationFallback.PagePath(filename);
                        // This cold Direct-open check uses managed I/O, independent of the
                        // optional native hot-path revocation probe and its platform bindings.
                        if (FileHelper.ExistsOrThrow(authority))
                            throw new IOException("Unpaired Shared coordination authority: '" + authority +
                                "'. Stop all connections to this database, then remove this orphan coordination file " +
                                "before retrying. Preserve database, WAL, backup and rebuild-recovery files.");
                    }
                }
                return guard;
            }
            catch (IOException error) when (!(error is DirectoryNotFoundException) && !(error is PathTooLongException))
            {
                lease?.Dispose();
                SharedCoordinationEvents.Log.Transition(filename, "mode-conflict", error.Message);
                throw new DatabaseAdmissionException(filename, error);
            }
            catch { lease?.Dispose(); throw; }
        }

        private static bool CanInitialize(FileStream stream)
        {
            var length = Math.Min(stream.Length, IdentityPrefix.Length);
            for (var i = 0; i < length; i++) if (stream.ReadByte() != IdentityPrefix[i]) return false;
            return true;
        }

        private static bool Matches(FileStream stream, byte[] identity)
        {
            if (stream.Length != identity.Length) return false;
            for (var i = 0; i < identity.Length; i++) if (stream.ReadByte() != identity[i]) return false;
            return true;
        }

        public void Dispose()
        {
            try { _legacyLease?.Dispose(); }
            finally { _lease.Dispose(); }
        }
    }

}
