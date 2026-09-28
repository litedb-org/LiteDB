using System.IO;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>Exception.Data key: an in-place overwrite refused because the log cannot sync.</summary>
        internal const string LogCannotBackOverwriteDataKey = "LiteDB.LogCannotBackOverwrite";

        /// <summary>
        /// The log answered "cannot sync" (#2242) at some sync of this engine: a checkpoint, promotion or
        /// conversion writes nothing, and the WAL limit holds (a checkpoint cannot drain the WAL).
        /// </summary>
        internal bool LogKnownUnsyncable => _logFlushDegraded && !_volatileLog;

        /// <summary>
        /// An in-place overwrite (a checkpoint's backfill, a format promotion, a conversion, the
        /// invalid-state mark) needs its recovery information on the device first, in both commit
        /// modes: durable commits=false gives up recent commits, never the data file's integrity
        /// (decisions 1 and D; SQLite's synchronous=NORMAL keeps its checkpoint barriers too). The
        /// latest log barrier answered "cannot sync": refuse before the overwrite and keep the WAL.
        /// With durable commits the barrier itself already threw. Caller holds the log writer lock.
        /// </summary>
        private void RequireLogSynced(string operation)
        {
            if (_logBarrierSynced || _volatileLog) return;
            var error = new IOException($"The log file cannot sync to the device (#2242): {operation} writes nothing " +
                "behind a recovery copy that is only in the operating system's cache. The log file is kept; " +
                "its commits stay readable, and a checkpoint drains it once the log file syncs.");
            error.Data[LogCannotBackOverwriteDataKey] = true;
            throw UnsyncedStorage(WriteFailure.InFile(error, FileOrigin.Log));
        }

        internal static bool IsLogCannotBackOverwrite(System.Exception error) => error.Data.Contains(LogCannotBackOverwriteDataKey);
    }
}
