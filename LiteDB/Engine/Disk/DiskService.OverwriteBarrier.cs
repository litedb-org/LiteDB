using System;
using System.IO;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>Exception.Data key: an in-place overwrite refused because the log cannot sync.</summary>
        internal const string LogCannotBackOverwriteDataKey = "LiteDB.LogCannotBackOverwrite";

        /// <summary>
        /// The log's latest barrier answered "cannot sync" (#2242): a checkpoint, promotion or conversion
        /// writes nothing, and the WAL limit holds (a checkpoint cannot drain the WAL). The next barrier
        /// that syncs (a checkpoint's, or <see cref="LogSyncs"/>) clears it.
        /// </summary>
        internal bool LogKnownUnsyncable => _logFlushDegraded && !_logBarrierSynced && !_volatileLog;

        /// <summary>
        /// Whether a checkpoint could drain the WAL into the data file as far as the log is concerned
        /// (the WAL limit, <c>$database.walKept</c>, deferring a checkpoint): true once a log barrier of
        /// this engine synced. Otherwise try one raw log sync: an engine that has not synced the log (a
        /// reopen, every operation of a shared connection) cannot know what an earlier one found. A sync
        /// that fails (an I/O error) propagates, naming the log file; it is recorded before the WAL
        /// writer is released (#3052), so a writer waiting for it is refused. A caller that passed its
        /// own check before a failure was published elsewhere syncs nothing once it holds the writer.
        /// </summary>
        internal bool LogSyncs(string operation = "A log sync")
        {
            if (_volatileLog || _readOnly || _logBarrierSynced) return true;
#if DEBUG || TESTING
            _state.BeforeSyncLock?.Invoke("log");
#endif
            lock (_writer.Value)
            {
                this.RequireNoFailureUnderLock();
                try { this.SyncRawLog(); }
                catch (Exception ex) when (this.PublishUnderLock(ex, FileOrigin.Log, operation)) { }
                // With durable commits the sync throws "cannot sync" (decision 3): the answer is false.
                catch (IOException ex) when (IsUnsyncedStorage(ex)) { }
            }
            return _logBarrierSynced;
        }

        /// <summary>
        /// An in-place overwrite (a checkpoint's backfill, a format promotion, a conversion, the
        /// invalid-state mark) needs its recovery information on the device first, in both commit
        /// modes: durable commits=false gives up recent commits, never the data file's integrity
        /// (decisions 1 and D; SQLite's synchronous=NORMAL keeps its checkpoint barriers too). The
        /// latest log barrier answered "cannot sync": refuse before the overwrite and keep the WAL.
        /// With durable commits the barrier itself already threw. A refusal before the operation wrote
        /// anything (<paramref name="wroteNothing"/>) is no failure (<see cref="IsQuietOverwriteRefusal"/>).
        /// Caller holds the log writer lock.
        /// </summary>
        private void RequireLogSynced(string operation, bool wroteNothing)
        {
            if (_logBarrierSynced || _volatileLog) return;
            var error = new IOException($"The log file cannot sync to the device (#2242): {operation} writes nothing " +
                "behind a recovery copy that is only in the operating system's cache. The log file is kept; " +
                "its commits stay readable, and a checkpoint drains it once the log file syncs.");
            error.Data[LogCannotBackOverwriteDataKey] = true;
            if (wroteNothing) error.Data[RefusedBeforeWriteDataKey] = true;
            throw UnsyncedStorage(WriteFailure.InFile(error, FileOrigin.Log));
        }

        internal static bool IsLogCannotBackOverwrite(System.Exception error) => error.Data.Contains(LogCannotBackOverwriteDataKey);

        /// <summary>
        /// An overwrite refused before it wrote anything because the log cannot sync, without durable
        /// commits: not a failure (proposed default A). A checkpoint keeps the WAL and returns 0, a
        /// compact write falls back to BSON, and any other operation's caller gets the refusal.
        /// </summary>
        internal static bool IsQuietOverwriteRefusal(System.Exception error) => IsLogCannotBackOverwrite(error) && IsRefusedBeforeWrite(error);
    }
}
