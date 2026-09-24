using System;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB.Engine
{
    /// <summary>
    /// Guards file-backed opens while rebuild installation has no confirmed live database.
    /// The marker is deliberately not an automatic recovery journal: its presence alone
    /// blocks opens, even when a crash or failed write left its diagnostic text incomplete.
    /// </summary>
    internal static class RebuildRecovery
    {
        private const int MarkerDeleteTimeoutSeconds = 5;

        internal static string GetMarkerFilename(string filename) => FileHelper.GetSuffixFile(filename, "-rebuild", false);

        internal static void EnsureAvailable(EngineSettings settings)
        {
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) ||
                settings.Filename == ":memory:" || settings.Filename == ":temp:") return;

            var marker = GetMarkerFilename(settings.Filename);
            try
            {
                // File.Exists hides access and IO errors. Only an absent marker is
                // evidence that it is safe to open (or create) the canonical database.
                File.GetAttributes(marker);
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (PathTooLongException)
            {
                // The sidecar name can exceed a limit even when the data/WAL names
                // fit. Establish absence by listing the parent, not by ignoring IO
                // errors. Conservative case/Unicode matching also covers aliases.
                var directory = Path.GetDirectoryName(Path.GetFullPath(settings.Filename));
                var name = Path.GetFileName(marker).Normalize();
                if (!Directory.EnumerateFileSystemEntries(directory).Any(entry =>
                    string.Equals(Path.GetFileName(entry).Normalize(), name, StringComparison.OrdinalIgnoreCase))) return;
            }

            throw new LiteException(LiteException.REBUILD_INCOMPLETE,
                "Rebuild recovery is incomplete. Database access is blocked by '{0}'. " +
                "Preserve the data, WAL, backup and temporary files; restore a complete database before removing this marker.", marker);
        }

        internal static void Begin(string filename, string backup, string backupLog, string candidate)
        {
#if DEBUG || TESTING
            RebuildService.SimulateInstallFailure?.Invoke("before-recovery-marker");
#endif
            // Never overwrite another installation's marker. If creation or flushing
            // fails, no database file has moved, and any partial marker stays blocking.
            using (var stream = new FileStream(GetMarkerFilename(filename), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes("LiteDB rebuild recovery\n" +
                    "Data: " + Path.GetFullPath(filename) + "\n" +
                    "WAL: " + Path.GetFullPath(FileHelper.GetLogFile(filename)) + "\n" +
                    "Backup data: " + Path.GetFullPath(backup) + "\n" +
                    "Backup WAL: " + Path.GetFullPath(backupLog) + "\n" +
                    "Replacement: " + Path.GetFullPath(candidate) + "\n");
                stream.Write(bytes, 0, bytes.Length);
#if DEBUG || TESTING
                RebuildService.SimulateInstallFailure?.Invoke("before-recovery-marker-flush");
#endif
                stream.FlushToDisk();
            }
        }

        internal static void Complete(string filename)
        {
#if DEBUG || TESTING
            RebuildService.SimulateInstallFailure?.Invoke("before-recovery-marker-delete");
#endif
            // Closing the new marker invites virus scanners and sync clients to open it, and on
            // Windows their handle fails the delete. Wait that out like the source rename does;
            // a marker that stays locked still fails here and keeps the database guarded.
            FileHelper.Exec(MarkerDeleteTimeoutSeconds, () => File.Delete(GetMarkerFilename(filename)));
        }
    }
}
