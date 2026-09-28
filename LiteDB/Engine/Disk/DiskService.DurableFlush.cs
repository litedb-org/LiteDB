using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private const int HR_ERROR_INVALID_FUNCTION = unchecked((int)0x80070001);
        private const int HR_ERROR_NOT_SUPPORTED = unchecked((int)0x80070032);
        private const int ERRNO_EINVAL = 22;
        private const int ERRNO_EROFS = 30;
        private const int ERRNO_ENOTSUP_BSD = 45;
        private const int ERRNO_ENOTSUP_LINUX = 95;

        private readonly bool _durableCommits;
        private readonly SharedDurabilityState _sharedDurability;
        private readonly bool _dataIsFile, _volatileLog;
        private volatile bool _logFlushDegraded;

        // The data file answered "cannot sync" (#2242): its barriers are ordered OS-cache flushes.
        private volatile bool _dataFlushDegraded;

        // Set once this engine proved what reusing a WAL slot needs (see ProveSlotReuse).
        private volatile bool _slotReuseProven;

        // Set by this engine's first successful data barrier: whatever earlier engines left in the
        // data file's OS cache is durable since (see ProveDataFile).
        private volatile bool _dataSyncProven;

        // Whether the latest data and log barrier synced (no data barrier yet: nothing unsynced);
        // false after "cannot sync" (#2242).
        private volatile bool _dataBarrierSynced = true, _logBarrierSynced;

        // Set once this engine made the WAL's directory entry durable. Until then a durable
        // commit is not acknowledged: syncing a new WAL does not persist its name on Unix.
        private volatile bool _logDirectorySynced;

        // The WAL's directory answered "cannot sync" (#2242): its name is not claimed durable.
        private volatile bool _logDirectoryUnsyncable;

        /// <summary>
        /// False when commits reach the OS cache only: the caller opted out
        /// (<see cref="EngineSettings.DurableCommits"/>), the log storage rejected a durable
        /// flush or its directory sync, or the log's syncs cannot report failures.
        /// </summary>
        internal bool IsLogFlushDurable => _durableCommits && !_logFlushDegraded && !_logDirectoryUnsyncable &&
            !_dataFlushDegraded && !this.LogSyncUnverified && !(_sharedDurability?.Degraded ?? false);

        /// <summary>
        /// A file WAL synced through the runtime's Flush(true) (no C library bound on Unix): the
        /// sync is still attempted, but a failure would go unreported, so it never proves
        /// durability. Such a log is not used for slot reuse (see <see cref="ProveSlotReuse"/>).
        /// </summary>
        private bool LogSyncUnverified => ((ChecksummedWalFactory)_logFactory).IsFile && NativeFileSync.UsesRuntimeSync;

        /// <summary>
        /// Some storage of this database answered "cannot sync" (#2242), in this engine or, in
        /// shared mode, an earlier one. Retirement witnesses need a durable sync and reused
        /// slots a durable clear, so such an engine neither retires nor reuses WAL frames.
        /// </summary>
        internal bool FlushDegraded => _logFlushDegraded || _dataFlushDegraded || (_sharedDurability?.FileSyncUnsupported ?? false);

        /// <summary>
        /// The latest data barrier answered "cannot sync" (#2242) while the latest log barrier
        /// synced: a backfill has not become durable, but a WAL change that discards its frames
        /// would at the next log sync. A WAL the engine keeps in memory protects nothing.
        /// </summary>
        internal bool DataUnsyncedWhileLogSyncs => !_volatileLog && !_dataBarrierSynced && _logBarrierSynced;

        /// <summary>
        /// Before a checkpoint retires frames, sync the data file and the log (and, once per engine,
        /// the log's directory), so storage that answers "cannot sync" (#2242), also storage that
        /// stopped syncing since this engine's last barrier, is found before a witness or a cleared
        /// slot depends on it. Retirement then does not run: a rooted data file needs its WAL, whose
        /// name is not durable without a directory sync. Only storage that stops syncing during
        /// the retiring checkpoint itself is detected later.
        /// Caller holds the log writer lock.
        /// </summary>
        internal bool ProveRetirementSyncs()
        {
            if (this.FlushDegraded || _logDirectoryUnsyncable) return false;
            var data = _dataPool.Writer.Value;
            lock (data) this.SyncDataBarrier(data);
            if (!this.FlushDegraded && !this.LogSyncUnverified) this.SyncRawLog();
            if (!this.FlushDegraded) this.SyncLogDirectory();
            return !this.FlushDegraded && !_logDirectoryUnsyncable;
        }

        /// <summary>
        /// Flush a confirmed WAL batch: to the device, or to the OS cache only when the caller opted out.
        /// Caller holds the log writer lock.
        /// </summary>
        private void FlushConfirmedLog(Stream stream)
        {
            if (_durableCommits) this.FlushLogToDisk(stream);
            else stream.Flush();
        }

        /// <summary>
        /// Opted-out or recovered commits may still reside in the OS cache when checkpoint starts.
        /// Sync the log first, so that a power loss during the checkpoint can still be redone from it.
        /// Caller holds the exclusive database lock.
        /// </summary>
        internal void SyncLogBeforeCheckpoint()
        {
            if (_readOnly) return;

            var stream = _writer.Value;

            lock (stream)
            {
                // Sync both the header recovery copy and preceding WAL before
                // overwriting data. A failed sync stops checkpoint before any data
                // overwrite; storage that cannot sync at all proceeds degraded (#2242).
                this.PrepareCheckpointHeader();
            }
        }

        /// <summary>
        /// Durably flush the log. Storage that cannot sync at all (some network shares and
        /// virtual file systems, #2242) is downgraded once to an OS-cache flush for the engine lifetime.
        /// Any other failure propagates: the caller must treat the commit outcome as unknown.
        /// Caller holds the log writer lock.
        /// </summary>
        private void FlushLogToDisk(Stream stream)
        {
            if (_logFlushDegraded)
            {
                stream.Flush();
                return;
            }

            if (!_dataSyncProven && _dataIsFile) this.ProveDataFile();
            this.SyncLogBarrier(stream);
            // The WAL may have been created (or recreated after a checkpoint deleted it) by
            // this or a crashed engine: make its name durable before a commit depends on it.
            if (!_logDirectorySynced) this.SyncLogDirectory();
        }

        /// <summary>
        /// Sync the log at a recovery barrier: checkpoint and header-marker journals, WAL tail
        /// repair and checksum conversion. A real sync is always attempted, even after commits
        /// degraded, so storage that recovers regains its power-loss guarantee. Storage that
        /// answers "cannot sync" (#2242) degrades like commits do: the ordered writes still
        /// reach the OS cache, which keeps the file consistent after a process crash, but
        /// power-loss safety is not claimed (the behaviour before #2818). Any other failure
        /// propagates, so the caller stops before overwriting data.
        /// </summary>
        private void SyncLogBarrier(Stream log)
        {
            try
            {
                log.FlushToDisk();
                _logBarrierSynced = true;
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                // The pages were written successfully; only the sync request was refused.
                _logBarrierSynced = false;
                log.Flush();
                this.MarkLogFlushDegraded(ex);
            }
        }

        /// <summary>
        /// Before this engine first reuses a WAL slot, prove that the data file and the log sync.
        /// Slots found at open were retired by an earlier engine, maybe of another connection,
        /// whose data file may have stopped syncing after that checkpoint's proof: the witness root
        /// that lets recovery skip a slot's old frame is then in the OS cache only, and this data
        /// sync makes it durable before the frame is overwritten. The log sync makes durable any
        /// clear an earlier engine wrote without one; it targets the raw log, so it adds no padding
        /// between a transaction's frames. (The WAL's name needs no sync here: the checkpoint that
        /// retired a slot synced its directory, and a WAL is deleted only when empty.) Storage that
        /// answers "cannot sync" (#2242) degrades, and the caller appends instead; that engine's
        /// commits then report reduced durability. Slots this engine retires later are proven again
        /// by their own checkpoint. Caller holds the log writer lock.
        /// </summary>
        private bool ProveSlotReuse()
        {
            if (_slotReuseProven) return true;
            // An unverifiable log sync proves nothing. A failed proof leaves the engine degraded,
            // so a retry per allocation costs no sync.
            if (this.FlushDegraded || this.LogSyncUnverified) return false;
            if (!_dataSyncProven) this.SyncDataFile();
            if (!this.FlushDegraded) this.SyncRawLog();
            return _slotReuseProven = !this.FlushDegraded;
        }

        /// <summary>
        /// A commit also depends on the data file, where an earlier engine, maybe of another
        /// connection or process, may have left a header in the OS cache only: a data file that
        /// answered "cannot sync" (#2242) for a salt rotation, a retirement root or a format
        /// conversion. Before this engine first acknowledges a commit as durable, make it durable;
        /// a "cannot sync" answer degrades this engine's commits instead. Only a header change can
        /// make earlier WAL content obsolete (a checkpoint that does not change it keeps every
        /// frame), so a shared connection whose latest data sync left this exact header skips it.
        /// Caller streams are the caller's to share. Caller holds the log writer lock.
        /// </summary>
        private void ProveDataFile()
        {
            var data = _dataPool.Writer.Value;
            lock (data)
            {
                var durable = _sharedDurability?.DurableHeader;
                if (durable != null && ReadDataHeader(data).SequenceEqual(durable)) _dataSyncProven = true;
                else this.SyncDataBarrier(data);
            }
        }

        /// <summary>Sync the data file as a barrier. Caller holds the log writer lock (order: log, then data).</summary>
        private void SyncDataFile()
        {
            var data = _dataPool.Writer.Value;
            lock (data) this.SyncDataBarrier(data);
        }

        private static byte[] ReadDataHeader(Stream data)
        {
            var position = data.Position;
            var header = new byte[PAGE_SIZE];
            data.Position = 0;
            data.ReadRequired(header, 0, PAGE_SIZE);
            data.Position = position;
            return header;
        }

        /// <summary>
        /// Sync the raw log (no padding between a transaction's frames). Storage that answers
        /// "cannot sync" (#2242) degrades. Caller holds the log writer lock.
        /// </summary>
        private void SyncRawLog()
        {
            var stream = _writer.Value;
            var raw = stream is ChecksummedWalStream wal ? wal.RawStream : stream;
            try
            {
                raw.FlushToDisk();
                _logBarrierSynced = true;
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                _logBarrierSynced = false;
                raw.Flush();
                this.MarkLogFlushDegraded(ex);
            }
        }

        /// <summary>
        /// Sync the data file at a barrier: creation, checkpoint, conversion, header publication and
        /// recovery. Storage that answers "cannot sync" (#2242) degrades like the log: the ordered
        /// writes reach the OS cache, which keeps the file consistent after a process crash, but
        /// power-loss safety is no longer claimed. Any other failure propagates.
        /// </summary>
        private void SyncDataBarrier(Stream data)
        {
            try
            {
                data.FlushToDisk();
                _dataBarrierSynced = _dataSyncProven = true;
                if (_sharedDurability != null && _dataIsFile) _sharedDurability.DurableHeader = ReadDataHeader(data);
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                _dataBarrierSynced = false;
                data.Flush();
                if (_sharedDurability != null) _sharedDurability.Degraded = _sharedDurability.FileSyncUnsupported = true;
                if (_dataFlushDegraded) return;
                _dataFlushDegraded = true;
                LOG($"data storage rejected durable flush ({ex.GetType().Name} 0x{ex.HResult:X8}); checkpoints now flush to the OS cache only", "DISK");
            }
        }

        private void MarkLogFlushDegraded(Exception ex)
        {
            if (_sharedDurability != null) _sharedDurability.Degraded = _sharedDurability.FileSyncUnsupported = true;
            if (_logFlushDegraded) return;
            _logFlushDegraded = true;
            LOG($"log storage rejected durable flush ({ex.GetType().Name} 0x{ex.HResult:X8}); commits now flush to the OS cache only", "DISK");
        }

        /// <summary>
        /// Sync the WAL directory entry unless the log storage already refused to sync:
        /// then no power-loss guarantee is claimed and the directory sync adds nothing.
        /// A directory that answers "cannot sync" (#2242) is reported through
        /// <see cref="IsLogFlushDurable"/>; file syncs continue. Any other failure propagates,
        /// so a commit fails and an overwrite does not start.
        /// </summary>
        private void SyncLogDirectory()
        {
            // An engine never deletes its WAL while open, and the WAL existed at the sync that
            // set the flag: its name is already durable.
            if (_logDirectorySynced || _logFlushDegraded || _logDirectoryUnsyncable) return;
            try
            {
                ((ChecksummedWalFactory)_logFactory).SyncDirectory();
                _logDirectorySynced = true;
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                _logDirectoryUnsyncable = true;
                if (_sharedDurability != null) _sharedDurability.Degraded = true;
                LOG($"log directory rejected a durable sync ({ex.GetType().Name} 0x{ex.HResult:X8}); a new WAL's name is not claimed durable", "DISK");
            }
        }

        /// <summary>
        /// True only for answers that mean "this handle cannot be synced", never for a failed sync.
        /// FlushFileBuffers: ERROR_ACCESS_DENIED (on a handle that was just written), ERROR_INVALID_FUNCTION,
        /// ERROR_NOT_SUPPORTED. fsync/F_FULLFSYNC: EINVAL, ENOTSUP/EOPNOTSUPP, EROFS, reported by
        /// <see cref="NativeFileSync"/> for file handles (released .NET runtimes lose Unix sync errors)
        /// and as a raw-errno HResult by other Unix streams.
        /// </summary>
        internal static bool IsDurableFlushUnsupported(Exception ex)
        {
            // Unix file handles are synced natively and report the raw errno.
            if (ex is FileSyncException sync) return sync.IsUnsupported;
            if (ex is UnauthorizedAccessException) return true;
            if (!(ex is IOException)) return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ex.HResult == HR_ERROR_INVALID_FUNCTION || ex.HResult == HR_ERROR_NOT_SUPPORTED;
            }

            return ex.HResult == ERRNO_EINVAL || ex.HResult == ERRNO_EROFS ||
                ex.HResult == ERRNO_ENOTSUP_BSD || ex.HResult == ERRNO_ENOTSUP_LINUX;
        }
    }
}
