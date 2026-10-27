using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteDB.Client.Shared;
using LiteDB.Utils;

namespace LiteDB.Engine
{
    // Immutable, checksummed extension of the rebuild guard, used only by ABI-2.
    // Until marker removal, recovery selects the original exact data/WAL pair.
    // No phase bit can tear or disagree with a rename: inode identities and the
    // presence of each name describe every forward/rollback/recovery boundary.
    internal static class MappedRebuildRecovery
    {
        internal const string RemovedMarkerKey = "LiteDB.MappedRebuild.MarkerRemoved";
        private const string Magic = "LiteDB mapped rebuild 1";
        internal static void Begin(EngineSettings settings, string backup, string backupLog, string candidate)
        {
            var filename = settings.Filename;
            var log = FileHelper.GetLogFile(filename);
            var fields = new[] { Magic, filename, backup, backupLog, candidate,
                Identity(filename), FileHelper.ExistsOrThrow(log) ? Identity(log) : "-", Identity(candidate) };
            byte[] payload;
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                    foreach (var field in fields) writer.Write(field);
                payload = memory.ToArray();
            }
            Stage("before-recovery-marker");
            var markerName = RebuildRecovery.GetMarkerFilename(filename);
            var pending = markerName + ".pending";
            // A death before the guard is published cannot have moved data/WAL.
            // Publish only the flushed complete record, never a buffered prefix.
            if (FileHelper.ExistsOrThrow(pending)) File.Delete(pending);
            using (var marker = new FileStream(pending, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                marker.Write(payload, 0, payload.Length);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(payload);
                marker.Write(hash, 0, hash.Length);
                Stage("marker-written");
                Stage("before-recovery-marker-flush");
                marker.FlushToDisk();
                Stage("marker-file-synced");
            }
            Sync(settings);
            File.Move(pending, markerName);
            Stage("marker-published");
            Sync(settings);
            Stage("marker-durable");
        }

        internal static bool TryRecover(EngineSettings settings)
        {
#if NET8_0_OR_GREATER
            if (!SharedMemoryAuthority.IsPlatformQualified) return false;
            using var path = NativeAdmission.EnterPath(settings.Filename);
            using var bootstrap = SharedMutexFactory.Create(SharedMutexNameFactory.CreateUsingSha1(
                SharedMemoryAuthority.PathFor(settings.Filename)) + ".Bootstrap");
            var held = false;
            try
            {
                try { held = bootstrap.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { held = true; }
                if (!held) return false;
                using var locks = new SharedRangeLocks(SharedMemoryAuthority.PathFor(settings.Filename));
                if (!locks.TryExclusiveDms()) return false;
                using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(settings.Filename, settings.SharedMutexNameStrategy));
                var mainHeld = false;
                try
                {
                    try { mainHeld = main.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (System.Threading.AbandonedMutexException) { mainHeld = true; }
                    if (!mainHeld) return false;
                    locks.EnterWrite(abandonedOwner: true);
                    Stage("recovery-exclusive");
                    var marker = RebuildRecovery.GetMarkerFilename(settings.Filename);
                    if (!FileHelper.ExistsOrThrow(marker)) return true;
                    var fields = Read(marker, settings.Filename);
                    if (fields == null) return false; // unknown/partial marker stays guarded
                    Restore(fields);
                    Stage("recovery-verified");
                    RebuildRecovery.Complete(settings.Filename, mapped: true);
                    Stage("recovery-complete");
                    return true;
                }
                finally
                {
                    try { locks.ExitWrite(); }
                    finally { if (mainHeld) main.ReleaseMutex(); }
                }
            }
            finally { if (held) bootstrap.ReleaseMutex(); }
#else
            return false;
#endif
        }

        private static string[] Read(string marker, string filename)
        {
            var bytes = File.ReadAllBytes(marker);
            if (bytes.Length < 32 || bytes.Length > 65536) return null;
            var length = bytes.Length - 32;
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, length).SequenceEqual(bytes.Skip(length))) return null;
            try
            {
                using var memory = new MemoryStream(bytes, 0, length);
                using var reader = new BinaryReader(memory, Encoding.UTF8);
                var fields = Enumerable.Range(0, 8).Select(_ => reader.ReadString()).ToArray();
                if (memory.Position != length || fields[0] != Magic || fields[1] != filename) return null;
                var directory = Path.GetDirectoryName(filename);
                for (var i = 2; i <= 4; i++)
                    if (Path.GetFullPath(fields[i]) != fields[i] || Path.GetDirectoryName(fields[i]) != directory ||
                        fields[i] == filename || fields[i] == FileHelper.GetLogFile(filename)) return null;
                if (fields.Skip(1).Take(4).Distinct().Count() != 4) return null;
                return fields;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is FormatException)
            { return null; }
        }

        private static void Restore(string[] fields)
        {
            var data = fields[1];
            var backup = fields[2];
            var backupLog = fields[3];
            var candidate = fields[4];
            var original = fields[5];
            var wal = fields[6];
            var replacement = fields[7];
            var log = FileHelper.GetLogFile(data);
            // Verify every participating name before the first recovery mutation.
            var liveIdentity = ExistingIdentity(data);
            var backupIdentity = ExistingIdentity(backup);
            var candidateIdentity = ExistingIdentity(candidate);
            var liveLog = ExistingIdentity(log);
            var savedLog = ExistingIdentity(backupLog);
            if ((liveIdentity != original && backupIdentity != original) ||
                (liveIdentity != null && liveIdentity != original && liveIdentity != replacement) ||
                (backupIdentity != null && backupIdentity != original) ||
                (candidateIdentity != null && candidateIdentity != replacement) ||
                (liveLog != null && liveLog != wal) || (savedLog != null && savedLog != wal) ||
                (wal != "-" && liveLog != wal && savedLog != wal) ||
                (liveIdentity == replacement && candidateIdentity != null))
                throw new IOException("Mapped rebuild recovery identities disagree. Preserve the guarded data/WAL, backups and replacement for inspection.");
            if (liveIdentity == replacement)
            {
                File.Move(data, candidate);
                NativeFileSync.SyncDirectory(Path.GetDirectoryName(data));
                Stage("recovery-candidate-retracted");
                liveIdentity = null;
            }
            if (liveIdentity == null)
            {
                File.Move(backup, data);
                NativeFileSync.SyncDirectory(Path.GetDirectoryName(data));
                Stage("recovery-source-restored");
            }
            if (liveLog == null && savedLog != null)
            {
                File.Move(backupLog, log);
                NativeFileSync.SyncDirectory(Path.GetDirectoryName(data));
                Stage("recovery-log-restored");
            }
            if (Identity(data) != original || (wal == "-" ? FileHelper.ExistsOrThrow(log) : Identity(log) != wal))
                throw new IOException("Mapped rebuild recovery could not establish the original data/WAL pair.");
            // Keep the unpublished replacement as evidence until the original pair
            // is confirmed. Recovery can die during cleanup and repeat safely.
            if (FileHelper.ExistsOrThrow(candidate)) File.Delete(candidate);
            Stage("recovery-candidate-cleaned");
        }

        internal static void Sync(EngineSettings settings)
        {
            if (settings.SharedOneFile) NativeFileSync.SyncDirectory(Path.GetDirectoryName(settings.Filename));
        }

        private static string ExistingIdentity(string path) => FileHelper.ExistsOrThrow(path) ? Identity(path) : null;
        private static string Identity(string path)
        {
            using var file = new DatabaseFileLock(path, true, false);
            return file.Identity;
        }
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        private static void Stage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("MappedRebuildStage");
            MappedRebuildStage?.Invoke(stage);
#endif
        }
#if DEBUG || TESTING
        internal static Action<string> MappedRebuildStage;
#endif
    }
}
