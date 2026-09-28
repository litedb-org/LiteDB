using System;
using System.IO;
using System.Threading;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // Writes this engine made to the data file, and how many of them its latest successful data
        // sync covered: -1 before its first one, since an earlier engine or process may have left
        // writes in the OS cache. Both change under the data writer's lock (UseDataWriter,
        // SyncDataBarrier); ShrinkLog reads them atomically, with or without the WAL writer's lock.
        private long _dataWrites, _dataWritesSynced = -1;

        /// <summary>
        /// The shared data writer's only accessor. Every positioned access (Position, Read, Write,
        /// SetLength, a sync) runs under its lock: a data sync may run outside the WAL writer's lock
        /// (<see cref="DataFileSyncs"/> before the WAL writer exists) while a checkpoint rewrites the
        /// header. Each write or SetLength inside calls <see cref="CountDataWrite"/> right before it.
        /// </summary>
        private T UseDataWriter<T>(Func<Stream, T> io)
        {
            var data = _dataPool.Writer.Value;
            lock (data) return io(data);
        }

        private void UseDataWriter(Action<Stream> io) => this.UseDataWriter(data =>
        {
            io(data);
            return true;
        });

        /// <summary>
        /// A write to the data file starts: a data sync must cover it before the log may shrink
        /// (<see cref="ShrinkLog"/>). Counted before the write, so one that fails half-way counts too;
        /// a sync that ran earlier (also inside the same UseDataWriter) does not cover it.
        /// Caller holds the data writer's lock.
        /// </summary>
        private void CountDataWrite() => Interlocked.Increment(ref _dataWrites);

        /// <summary>A data sync that succeeded covered <paramref name="covered"/> writes. Caller holds the data writer's lock.</summary>
        private void DataWritesSynced(long covered)
        {
            if (covered > Interlocked.Read(ref _dataWritesSynced)) Interlocked.Exchange(ref _dataWritesSynced, covered);
        }

        /// <summary>
        /// The one way to shrink the log where recovery may still need what goes: WAL frames, a header's
        /// recovery copy (the header journal), a legacy header backup. Every data write this engine made
        /// must be covered by a successful data sync, and it must have made one: otherwise the OS could
        /// write the shrunk log back ahead of the data file, and a power loss would lose what only the log
        /// held. Fails closed with <see cref="DataStoppedSyncing"/>, before anything is removed. A WAL kept
        /// in memory survives no power loss and is exempt. Bytes no recovery reads (a torn tail, a partial
        /// trailing page, a failed append's own frame) are trimmed without it.
        /// </summary>
        internal void ShrinkLog(Stream log, long length, string operation)
        {
            // An outstanding header journal is retired with it, also where the log keeps its length.
            if (log.Length > length || _checksums.JournalBytes != 0) this.RequireDataWritesSynced(operation);
            log.SetLength(length);
        }

        /// <summary>
        /// Empty the log through the same check as <see cref="ShrinkLog"/>, and reset its positions. The
        /// raw log counts, not only its WAL frames: a conversion's log holds the legacy header backup and
        /// the conversion journal after its drain emptied the WAL.
        /// </summary>
        internal void EmptyLog(string operation)
        {
            var raw = _writer.IsValueCreated ? (_writer.Value as ChecksummedWalStream)?.RawStream ?? _writer.Value : null;
            if (this.GetFileLength(FileOrigin.Log) > 0 || _checksums.JournalBytes != 0 || (raw?.Length ?? 0) > 0)
                this.RequireDataWritesSynced(operation);
            this.SetLength(0, FileOrigin.Log);
        }

        /// <summary>
        /// Stop (<see cref="DataStoppedSyncing"/>) unless a successful data sync of this engine covered
        /// every data write it made: also before clearing retired WAL slots (ReclaimLogPages).
        /// </summary>
        internal void RequireDataWritesSynced(string operation)
        {
            if (_volatileLog) return;
            if (Interlocked.Read(ref _dataWritesSynced) < Interlocked.Read(ref _dataWrites)) throw DataStoppedSyncing(operation);
        }

        /// <summary>
        /// Record a write or sync failure before the stop it causes (decision 6 of
        /// docs/decisions/durability-policy.md): the engine then reopens read-only on its next call and
        /// refuses writes until the database is reopened.
        /// </summary>
        internal void RecordWriteFailure(string operation, Exception error)
        {
            bool walKept;
            try { walKept = this.GetFileLength(FileOrigin.Log) > 0; }
            catch (Exception) { walKept = true; }
            _state.RecordWriteFailure(new WriteFailure(operation, error, walKept));
        }

        /// <summary>For an exception filter: name the file of a failed write or sync, and let it pass.</summary>
        private static bool FailedIn(Exception error, FileOrigin origin)
        {
            WriteFailure.InFile(error, origin);
            return false;
        }
    }
}
