using System;
using System.IO;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Record a write or sync failure before the stop it causes (decision 6 of
        /// docs/decisions/durability-policy.md): the engine then reopens read-only on its next call and
        /// refuses writes until the database is reopened. walKept counts whatever the log file holds, not
        /// only the WAL's frames: an outstanding header journal is kept like the WAL (decision 1).
        /// </summary>
        internal void RecordWriteFailure(string operation, Exception error, AcknowledgedLog acknowledged = null)
        {
            bool walKept;
            try { walKept = this.LogHoldsAnything(); }
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
        /// For an exception filter around a WAL batch write: the failure is the log file's (decision 6
        /// records the file), unless a deeper failure named another. An engine that already stopped
        /// throws its stop error, which names nothing. Lets the failure pass.
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

        /// <summary>
        /// For <c>$database.walKept</c>: the WAL holds frames kept until a data sync succeeds. An engine
        /// whose latest data sync did not succeed, or that tried none yet (a reopen, a restart or a
        /// shared-mode operation after an engine that kept the WAL), retries one first, as its next
        /// checkpoint would. A data sync that fails there (an I/O error, not "cannot sync") does not fail
        /// the read: it is recorded, reported, and the engine's next call stops it and reopens it
        /// read-only (decision 6). A read-only engine or one over storage that cannot be written never
        /// syncs: it reports what the connection's engines found, and the write failure it reopened after.
        /// </summary>
        internal bool WalKeptReport
        {
            get
            {
                if (_volatileLog || this.GetFileLength(FileOrigin.Log) == 0) return false;
                if (_readOnly || _readOnlyStorage)
                    return (_sharedDurability?.DataUnsynced ?? false) || (_state.ReopenedAfter?.WalKept ?? false);
                // A log that cannot sync keeps the WAL too: a checkpoint writes nothing behind it.
                if (this.LogKnownUnsyncable) return true;
                if (this.DataSyncConfirmed) return false;
                try { return !this.DataFileSyncs(); }
                catch (IOException ex)
                {
                    _state.StopLater("A data sync", WriteFailure.InFile(ex, FileOrigin.Data));
                    return true;
                }
            }
        }
    }
}
