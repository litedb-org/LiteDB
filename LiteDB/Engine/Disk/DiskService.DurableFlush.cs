using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        private readonly bool _dataIsFile;

        // The WAL lives in memory (EngineSettings.VolatileLog): it survives no power loss.
        private readonly bool _volatileLog;

        // The data file's full path, for DurableHeaders; null when it has none.
        private readonly string _dataPath;

        // The log file's full path, for DurableLogs; null when it is not a file with a path.
        private readonly string _logPath;
        private volatile bool _logFlushDegraded;

        // The data file answered "cannot sync" (#2242): its barriers are ordered OS-cache flushes.
        private volatile bool _dataFlushDegraded;

        // Set once this engine proved what reusing a WAL slot needs (see ProveSlotReuse).
        private volatile bool _slotReuseProven;

        // Set by this engine's first successful data barrier: whatever earlier engines left in the
        // data file's OS cache is durable since (see ProveDataFile).
        private volatile bool _dataSyncProven;

        // Whether the latest data and log barrier synced (no data barrier yet: nothing unsynced);
        // false after "cannot sync" (#2242), and for the log also while its sync waits for the data file.
        private volatile bool _dataBarrierSynced = true, _logBarrierSynced;

        // Set by this engine's first data barrier, whatever it answered.
        private volatile bool _dataBarrierTried;

        // Set once this engine made the WAL's directory entry durable. Until then a durable
        // commit is not acknowledged: syncing a new WAL does not persist its name on Unix.
        private volatile bool _logDirectorySynced;

        // The WAL's directory answered "cannot sync" (#2242): its name is not claimed durable.
        private volatile bool _logDirectoryUnsyncable;

        /// <summary>
        /// False when commits are not made durable: the caller opted out
        /// (<see cref="EngineSettings.DurableCommits"/>), the log storage rejected a durable flush or its
        /// directory sync (commits then reach the OS cache only), the log's syncs cannot report
        /// failures, or a write or sync of the log failed. A data file that cannot sync does not change
        /// it: commits stay durable in the WAL, which <c>$database.walKept</c> reports.
        /// </summary>
        internal bool IsLogFlushDurable => _durableCommits && !_logFlushDegraded && !_logDirectoryUnsyncable &&
            !this.LogSyncUnverified && !(_sharedDurability?.Degraded ?? false) && !this.LogWriteFailed;

        /// <summary>
        /// A file WAL synced through the runtime's Flush(true) (no C library bound on Unix): the
        /// sync is still attempted, but a failure would go unreported, so it never proves
        /// durability. Such a log is not used for slot reuse (see <see cref="ProveSlotReuse"/>).
        /// </summary>
        internal bool LogSyncUnverified => ((ChecksummedWalFactory)_logFactory).IsFile && NativeFileSync.UsesRuntimeSync;

        /// <summary>
        /// Some storage of this database answered "cannot sync" (#2242), in this engine or, in
        /// shared mode, an earlier one. Retirement witnesses need a durable sync and reused
        /// slots a durable clear, so such an engine neither retires nor reuses WAL frames.
        /// </summary>
        internal bool FlushDegraded => _logFlushDegraded || _dataFlushDegraded || (_sharedDurability?.FileSyncUnsupported ?? false);

        /// <summary>
        /// The latest data barrier answered "cannot sync" (#2242), so the data file may not hold on
        /// the device what the WAL does. The WAL is emptied only after a data sync that covers the
        /// backfill succeeded (<see cref="ShrinkLog"/> enforces it): until one does, a full checkpoint
        /// keeps it and the WAL grows (reported as <c>$database.walKept</c>). Whether an earlier engine or process synced WAL
        /// frames is not known here, and the OS can write an emptied WAL back ahead of the backfill.
        /// A WAL the engine keeps in memory protects nothing across a power loss and is emptied.
        /// </summary>
        internal bool KeepsWal => !_volatileLog && !_dataBarrierSynced;

        /// <summary>
        /// This engine's latest data sync succeeded. False before its first one: an earlier engine or
        /// process may have found that the data file cannot sync and kept the WAL, which this one
        /// cannot tell from the files.
        /// </summary>
        internal bool DataSyncConfirmed => _dataBarrierTried && _dataBarrierSynced;

        /// <summary>
        /// Once this engine or, in shared mode, an earlier engine of the connection found that the data
        /// file cannot sync, a checkpoint retries the data sync first and does nothing while it still
        /// fails, instead of scanning the growing WAL again. Storage that syncs pays nothing here; an
        /// engine that knows nothing yet finds out at its checkpoint's pre-write sync.
        /// </summary>
        internal bool DefersCheckpoint() =>
            !_volatileLog && (((!_dataBarrierSynced || (_sharedDurability?.DataUnsynced ?? false)) && !this.DataFileSyncs("A checkpoint")) ||
            // So while this engine found that the log cannot sync (decision D): no journal could back the
            // checkpoint's overwrite, and a checkpoint would scan the WAL at every commit to find that out.
            (this.LogKnownUnsyncable && !this.LogSyncs("A checkpoint")));

        /// <summary>
        /// Sync the data file now: false when it answers "cannot sync" (#2242). A checkpoint calls it
        /// right before it writes anything, so that it writes to a data file that just synced, whatever
        /// an earlier engine or process found (see <see cref="KeepsWal"/>).
        /// </summary>
        internal bool DataFileSyncs(string operation = "A data sync")
        {
#if DEBUG || TESTING
            _state.BeforeSyncLock?.Invoke("data");
#endif
            // Ordered with the WAL writer's barriers once it exists. Taking its lock would create it,
            // and an encrypted WAL writes and syncs its preamble when created: not for a sync alone.
            // The data writer's lock orders it with every other data write either way. The failure
            // boundary (#3052) sits inside the innermost lock: a recheck once it is held, and a real
            // I/O failure recorded before it is released.
            if (_writer.IsValueCreated) lock (_writer.Value) this.SyncDataFileUnderLock(operation);
            else this.SyncDataFileUnderLock(operation);
            return _dataBarrierSynced;
        }

        private void SyncDataFileUnderLock(string operation) => this.UseDataWriter(data =>
        {
            this.RequireNoFailureUnderLock();
            try { this.SyncDataBarrier(data); }
            catch (Exception ex) when (this.PublishUnderLock(ex, FileOrigin.Data, operation)) { }
        });

        /// <summary>
        /// A data sync of a checkpoint (or a recovery) that already wrote pages answered "cannot sync":
        /// the data file stopped syncing since the sync that let it start. What the WAL and its header
        /// journal hold is the only durable copy, so the caller stops before removing any of it.
        /// </summary>
        internal static IOException DataStoppedSyncing(string operation) => UnsyncedStorage(WriteFailure.InFile(new IOException(
            $"The data file stopped syncing to the device during {operation}: the log file and its header recovery " +
            "copy are kept, and the database must be reopened once the storage syncs."), FileOrigin.Data));

        /// <summary>Exception.Data key: refused or stopped because the data file cannot sync (#2242).</summary>
        internal const string UnsyncedStorageDataKey = "LiteDB.UnsyncedStorage";

        internal static IOException UnsyncedStorage(IOException error)
        {
            error.Data[UnsyncedStorageDataKey] = true;
            return error;
        }

        /// <summary>
        /// The operation was refused or stopped because the data file cannot sync (#2242), before it
        /// removed the log or its header's recovery copy: an open that failed so can read instead.
        /// </summary>
        internal static bool IsUnsyncedStorage(Exception error) => error.Data.Contains(UnsyncedStorageDataKey);

        /// <summary>The engine keeps its WAL in memory (<see cref="EngineSettings.VolatileLog"/>).</summary>
        internal bool LogIsVolatile => _volatileLog;

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
            // #3052: the checkpoint's admission check ran before it took the writer; nothing syncs on
            // handles whose failure was published meanwhile. A failure here is recorded by the
            // checkpoint before it releases the writer (BeginCheckpointStop).
            this.RequireNoFailureUnderLock();
            this.SyncDataFile();
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
        internal bool SyncLogBeforeCheckpoint()
        {
            if (_readOnly) return true;

            var stream = _writer.Value;

            lock (stream)
            {
                // #3052: recheck once the writer is held. A failed journal write or sync is recorded by
                // the checkpoint, which holds the writer, before it releases it (BeginCheckpointStop).
                this.RequireNoFailureUnderLock();
                // Sync both the header recovery copy and preceding WAL before overwriting data. A
                // failed sync stops the checkpoint before any data overwrite; a log that cannot sync
                // (#2242) refuses it before the journal is written: false, nothing written, WAL kept.
                try { this.PrepareCheckpointHeader(); }
                catch (IOException ex) when (IsQuietOverwriteRefusal(ex)) { return false; }
                return true;
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

            this.SyncLogBarrier(stream);
            // The WAL may have been created (or recreated after a checkpoint deleted it) by
            // this or a crashed engine: make its name durable before a commit depends on it.
            // A WAL is deleted only once empty, after a data sync covered its backfill (KeepsWal).
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
                // The pages were written successfully; only the sync request was refused. Commits
                // degrade to the OS cache; an overwrite behind this barrier is refused instead
                // (RequireLogSynced), in both commit modes.
                _logBarrierSynced = false;
                log.Flush();
                this.MarkLogFlushDegraded(ex);
            }
            catch (Exception ex) when (FailedIn(ex, FileOrigin.Log))
            {
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
        /// by their own checkpoint. This is the conservative baseline: every fresh engine (every
        /// shared-mode operation) pays one proof before its first reuse. Caller holds the log writer lock.
        /// </summary>
        private bool ProveSlotReuse()
        {
            if (_slotReuseProven) return true;
            // An unverifiable log sync proves nothing. A failed proof leaves the engine degraded,
            // so a retry per allocation costs no sync.
            if (this.FlushDegraded || this.LogSyncUnverified) return false;
            if (!_dataSyncProven)
            {
                if (_dataIsFile) this.ProveDataFile();
                else this.SyncDataFile();
            }
            if (!this.FlushDegraded) this.SyncRawLog();
            return _slotReuseProven = !this.FlushDegraded;
        }

        /// <summary>
        /// Prove that pages left in the data file's OS cache (a file someone copied into place, an
        /// earlier engine's writes, maybe of another connection) are on the device: sync the data
        /// file, unless its header is one a successful sync in this process left
        /// (<see cref="DurableHeaders"/>). Only a header change can make earlier WAL content
        /// obsolete (a checkpoint that does not change it keeps every frame). Caller holds the log
        /// writer lock.
        /// </summary>
        private void ProveDataFile()
        {
            this.UseDataWriter(data =>
            {
                if (_dataPath != null && data.Length >= PAGE_SIZE && DurableHeaders.Matches(_dataPath, ReadDataHeader(data))) _dataSyncProven = true;
                else this.SyncDataBarrier(data);
            });
        }

        private static string DurablePath(EngineSettings settings)
        {
            var path = settings.DataStream == null ? settings.Filename : (settings.DataStream as FileStream)?.Name;
            if (string.IsNullOrEmpty(path) || path == ":memory:" || path == ":temp:") return null;
            try { return Path.IsPathRooted(path) || settings.DataStream == null ? Path.GetFullPath(path) : null; }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        private static string LogDurablePath(EngineSettings settings)
        {
            if (settings.LogStream != null) return (settings.LogStream as FileStream)?.Name is string name && Path.IsPathRooted(name) ? Path.GetFullPath(name) : null;
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) || settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;
            try { return Path.GetFullPath(FileHelper.GetLogFile(settings.Filename)); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        /// <summary>
        /// Sync the data file as a barrier, under the data writer's lock. A caller that also takes the
        /// log writer's lock takes it first (order: log, then data); an open, a conversion and
        /// <see cref="DataFileSyncs"/> before the WAL writer exists take only the data writer's.
        /// </summary>
        private void SyncDataFile()
        {
            this.UseDataWriter(this.SyncDataBarrier);
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
            catch (Exception ex) when (FailedIn(ex, FileOrigin.Log))
            {
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
            _dataBarrierTried = true;
            // Caller holds the data writer's lock (UseDataWriter): no write can slip in before the sync.
            var covered = Interlocked.Read(ref _dataWrites);
            try
            {
                data.FlushToDisk();
                this.DataWritesSynced(covered);
                _dataBarrierSynced = _dataSyncProven = true;
                if (_sharedDurability != null) _sharedDurability.DataUnsynced = false;
                if (_dataPath != null && data.Length >= PAGE_SIZE) DurableHeaders.Record(_dataPath, ReadDataHeader(data));
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                _dataBarrierSynced = false;
                data.Flush();
                // Commits stay durable in the WAL (decision 4): only retirement and the WAL's removal wait.
                if (_sharedDurability != null) _sharedDurability.FileSyncUnsupported = _sharedDurability.DataUnsynced = true;
                if (_dataFlushDegraded) return;
                _dataFlushDegraded = true;
                LOG($"data storage rejected durable flush ({ex.GetType().Name} 0x{ex.HResult:X8}); checkpoints now flush to the OS cache only", "DISK");
            }
            catch (Exception ex) when (FailedIn(ex, FileOrigin.Data))
            {
            }
        }

        private void MarkLogFlushDegraded(Exception ex)
        {
            DurableLogs.Forget(_logPath);
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
                DurableLogs.Forget(_logPath);
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
