using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// The sweep's Durable oracle: a cold Direct reopen of the case's database, checked against its ledger.
    /// <para>
    /// A Direct reopen opens the data file for writing with <see cref="FileShare.Read"/>. Windows refuses that
    /// open with a sharing violation while any other handle can write the file, even one a leaked stream or a
    /// Shared connection's handle cache left behind; POSIX does not. When the reopen fails that way and the
    /// Quiescent oracle already reported a handle to the data file or its WAL as still open, the refusal is
    /// that reported leftover, which is judged on its own as <c>quiescent.handles</c> (excused only where a skip
    /// leaves it, matched only by a registered finding that lists it). Durable then checks a byte copy of the
    /// data file and its WAL in a fresh directory instead: those bytes are what a reopen after the handle
    /// closes, or after this process exits, reads. Lost, changed or resurrected data on the copy still fails,
    /// and so does a copy that cannot be made or opened. The case records that the copy was used.
    /// </para>
    /// </summary>
    internal static class TeardownDurable
    {
        private const int SharingViolation = unchecked((int)0x80070020); // HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)
        private const int LockViolation = unchecked((int)0x80070021); // HRESULT_FROM_WIN32(ERROR_LOCK_VIOLATION)

        /// <summary>Subdirectory of the case directory the byte copy is made in.</summary>
        public const string CopyDirectory = "durable-copy";

        /// <param name="openHandles">Handles the Quiescent oracle reported still open at scenario end (full paths).</param>
        /// <param name="open">Opens a database; a seam for the oracle's self-tests (default: <see cref="LiteDatabase"/>).</param>
        public static void Check(TeardownCase c, IReadOnlyCollection<string> openHandles, TeardownRunResult result,
            Func<ConnectionString, ILiteDatabase> open = null)
        {
            open = open ?? (connection => new LiteDatabase(connection));
            var reopen = Verify(c, c.DatabasePath, open, result.Violations);
            if (reopen == null) return;
            if (!BlockedByReportedHandle(reopen, c.DatabasePath, openHandles))
            {
                result.Violations.Add("durable.REOPEN_FAILED: the cold reopen failed: " + TeardownSweep.Describe(reopen));
                return;
            }
            string copy;
            try { copy = Copy(c.DatabasePath, Path.Combine(c.Directory, CopyDirectory)); }
            catch (Exception error)
            {
                result.Violations.Add("durable.REOPEN_FAILED: the cold reopen failed: " + TeardownSweep.Describe(reopen) +
                    "; copying the database files failed too: " + TeardownSweep.Describe(error));
                return;
            }
            var onCopy = Verify(c, copy, open, result.Violations);
            if (onCopy != null)
            {
                result.Violations.Add("durable.REOPEN_FAILED: the cold reopen failed: " + TeardownSweep.Describe(reopen) +
                    "; reopening a byte copy of the database files failed too: " + TeardownSweep.Describe(onCopy));
                return;
            }
            result.Notes.Add("durable: checked on a byte copy of the database files; the cold reopen of the original hit " +
                "a sharing violation from a handle quiescent.handles reports: " + TeardownSweep.Describe(reopen));
        }

        /// <summary>
        /// True when <paramref name="reopen"/> is a Windows sharing or lock violation and a handle to the data
        /// file or its WAL is among <paramref name="openHandles"/>: the refusal is that reported handle.
        /// </summary>
        public static bool BlockedByReportedHandle(Exception reopen, string database, IReadOnlyCollection<string> openHandles)
        {
            var family = Family(database);
            if (openHandles == null || !openHandles.Any(handle => family.Contains(handle, StringComparer.OrdinalIgnoreCase))) return false;
            for (var error = reopen; error != null; error = error.InnerException)
            {
                if (error is IOException && (error.HResult == SharingViolation || error.HResult == LockViolation)) return true;
            }
            return false;
        }

        /// <summary>
        /// Copy the data file and its WAL (when present) into <paramref name="directory"/> under their own names,
        /// reading with full sharing so the leftover handle does not block the read. Returns the copy's data file.
        /// </summary>
        public static string Copy(string database, string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var source in Family(database).Where(File.Exists))
            {
                var target = Path.Combine(directory, Path.GetFileName(source));
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
            return Path.Combine(directory, Path.GetFileName(database));
        }

        private static string[] Family(string database)
        {
            var full = Path.GetFullPath(database);
            return new[] { full, FileHelper.GetLogFile(full) };
        }

        /// <summary>Reopen <paramref name="path"/> and add the ledger's violations; returns the reopen failure, if any.</summary>
        private static Exception Verify(TeardownCase c, string path, Func<ConnectionString, ILiteDatabase> open, List<string> violations)
        {
            try
            {
                var connection = new ConnectionString
                {
                    Filename = path, Connection = ConnectionType.Direct, Password = c.Password, AutoRebuild = c.ReopenWithAutoRebuild
                };
                using (var db = open(connection))
                    foreach (var item in c.Ledger.Verify(db)) violations.Add("durable." + item);
                return null;
            }
            catch (Exception error) { return error; }
        }
    }
}
