using System;
using System.IO;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Record a write or sync failure before the stop it causes (decision 6 of
        /// docs/decisions/durability-policy.md): the engine then reopens read-only on its next call and
        /// refuses writes until the database is reopened. walKept counts whatever the log file holds, not only the
        /// WAL's frames: an outstanding header journal is kept like the WAL (decision 1).
        /// </summary>
        internal void RecordWriteFailure(string operation, Exception error, AcknowledgedLog acknowledged = null)
        {
            bool walKept;
            // A WAL the engine keeps in memory is no log file anyone could keep.
            try { walKept = !_volatileLog && this.LogHoldsAnything(); }
            catch (Exception) { walKept = true; }
            _state.RecordWriteFailure(new WriteFailure(operation, error, walKept, acknowledged));
        }

        /// <summary>
        /// The log file holds something recovery may need: WAL frames, an outstanding header journal, a
        /// legacy header backup (a conversion's, after its drain emptied the WAL). The raw log counts;
        /// an encrypted log's preamble does not.
        /// </summary>
        private bool LogHoldsAnything()
        {
            var raw = _writer.IsValueCreated ? (_writer.Value as ChecksummedWalStream)?.RawStream ?? _writer.Value : null;
            return this.GetFileLength(FileOrigin.Log) > 0 || _checksums.JournalBytes != 0 || (raw?.Length ?? 0) > 0;
        }

        /// <summary>
        /// For an exception filter around a WAL batch write: the failure is the log file's, unless a
        /// deeper failure named another. An engine that already stopped throws its stop error, which
        /// names nothing. Lets the failure pass.
        /// </summary>
        internal bool NameLogWriteFailure(Exception error)
        {
            if (!_state.Stopped) WriteFailure.InFile(error, FileOrigin.Log);
            return false;
        }

        /// <summary>
        /// A write or sync of the log file failed (this engine's record, or the one it reopened read-only
        /// after): no commit is durable there, so <see cref="IsLogFlushDurable"/> reads false.
        /// </summary>
        private bool LogWriteFailed => (_state.WriteFailure ?? _state.ReopenedAfter)?.File == "log";

        /// <summary>See <see cref="EngineState.RequireNoWriteFailure"/>.</summary>
        internal void RequireNoWriteFailure() => _state.RequireNoWriteFailure();

        /// <summary>
        /// #3052, after acquiring the lock that orders a sync and before issuing it: a caller's admission
        /// check ran before it waited for the lock, and the engine may have stopped, or recorded a failure,
        /// meanwhile. Nothing more is synced or written through handles whose failure was published.
        /// </summary>
        private void RequireNoFailureUnderLock()
        {
            _state.ThrowIfStopped();
            _state.RequireNoWriteFailure();
        }

        /// <summary>See <see cref="RequireNoFailureUnderLock"/>; caller (a checkpoint) holds the WAL writer.</summary>
        internal void RequireNoFailureUnderWriter() => this.RequireNoFailureUnderLock();

        /// <summary>
        /// #3052, for an exception filter inside the lock that orders a sync: record a real I/O failure of
        /// it before the lock is released, so that every waiter's recheck (<see cref="RequireNoFailureUnderLock"/>,
        /// a WAL batch's admission) refuses. Storage that answers "cannot sync" (#2242) is no failure and is
        /// not recorded; nor is a refusal that an earlier record or stop caused. The first failure stays the
        /// one reported. Teardown, if the caller stops the engine, follows outside the lock. Lets it pass.
        /// </summary>
        private bool PublishUnderLock(Exception error, FileOrigin origin, string operation)
        {
            if (error is IOException && !IsUnsyncedStorage(error) && !IsDurableFlushUnsupported(error) &&
                !_state.Stopped && _state.WriteFailure == null)
            {
                this.RecordWriteFailure(operation, WriteFailure.InFile(error, origin));
            }
            return false;
        }

        /// <summary>
        /// For <c>$database.walKept</c>: the WAL holds frames kept until a data sync succeeds, or while the
        /// log cannot sync (no checkpoint overwrites behind it). An engine whose latest data or log sync
        /// did not succeed, or that tried none yet (a reopen, a restart or a shared-mode operation after
        /// an engine that kept the WAL), retries one first, as its next checkpoint would. A sync that
        /// fails there (an I/O error, not "cannot sync") does not fail the read: the helper recorded it
        /// before it released its lock (#3052), it is reported here, every later write, sync and
        /// checkpoint is refused, and the stop is due (<see cref="EngineState.StopLater"/>). A read-only
        /// engine never syncs: it reports what the connection's engines found, and the write failure it
        /// reopened after.
        /// </summary>
        internal bool WalKeptReport
        {
            get
            {
                if (_volatileLog || this.GetFileLength(FileOrigin.Log) == 0) return false;
                if (_readOnly) return (_sharedDurability?.DataUnsynced ?? false) || (_state.ReopenedAfter?.WalKept ?? false);
                // After a recorded failure nothing syncs again on its handles (fsyncgate): report the record's.
                if (_state.WriteFailure is WriteFailure failure) return failure.WalKept;
                try
                {
                    // A log that cannot sync keeps the WAL too: a checkpoint writes nothing behind it.
                    if (!this.LogSyncs()) return true;
                    if (this.DataSyncConfirmed) return false;
                    return !this.DataFileSyncs();
                }
                catch (IOException ex) when (_state.WriteFailure != null)
                {
                    // Recorded under the lock by the sync that failed, or by another operation while this
                    // read waited for the lock (its recheck then synced nothing): report, and stop later.
                    _state.StopLater("A sync", ex);
                    return _state.WriteFailure.WalKept;
                }
            }
        }
    }
}
