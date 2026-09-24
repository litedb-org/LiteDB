using System;
using System.IO;
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
        private volatile bool _logFlushDegraded;

        // Set by this engine's first successful log device sync. That sync also makes
        // blank slots found at open durable, even if an earlier engine cleared them on
        // storage that could not sync, so reclaimed slots are reused only after it
        // (see ProveLogSync).
        private volatile bool _logSyncProven;

        /// <summary>
        /// False when commits reach the OS cache only: the caller opted out
        /// (<see cref="EngineSettings.DurableCommits"/>) or the log storage rejected a durable flush.
        /// </summary>
        internal bool IsLogFlushDurable => _durableCommits && !_logFlushDegraded &&
            !(_sharedDurability?.Degraded ?? false);

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

            this.SyncLogBarrier(stream);
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
                _logSyncProven = true;
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                // The pages were written successfully; only the sync request was refused.
                log.Flush();
                this.MarkLogFlushDegraded(ex);
            }
        }

        /// <summary>
        /// Before this engine first reuses a slot found blank at open, prove that the log can
        /// sync. The sync also makes clears an earlier engine wrote without one durable. It
        /// targets the raw log, so it adds no padding between a transaction's frames. Storage
        /// that answers "cannot sync" (#2242) degrades, and the caller appends instead; that
        /// engine's commits then skip their own rejected sync, so the probe costs nothing extra.
        /// Caller holds the log writer lock.
        /// </summary>
        private bool ProveLogSync()
        {
            if (_logSyncProven) return true;
            if (_logFlushDegraded) return false;
            var stream = _writer.Value;
            var raw = stream is ChecksummedWalStream wal ? wal.RawStream : stream;
            try
            {
                raw.FlushToDisk();
                _logSyncProven = true;
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                raw.Flush();
                this.MarkLogFlushDegraded(ex);
            }
            return _logSyncProven;
        }

        private void MarkLogFlushDegraded(Exception ex)
        {
            if (_sharedDurability != null) _sharedDurability.Degraded = true;
            if (_logFlushDegraded) return;
            _logFlushDegraded = true;
            LOG($"log storage rejected durable flush ({ex.GetType().Name} 0x{ex.HResult:X8}); commits now flush to the OS cache only", "DISK");
        }

        /// <summary>
        /// Sync the WAL directory entry unless the log storage already refused to sync:
        /// then no power-loss guarantee is claimed and the directory sync adds nothing.
        /// </summary>
        private void SyncLogDirectory()
        {
            if (!_logFlushDegraded) ((ChecksummedWalFactory)_logFactory).SyncDirectory();
        }

        /// <summary>
        /// True only for answers that mean "this handle cannot be synced", never for a failed sync.
        /// FlushFileBuffers: ERROR_ACCESS_DENIED (on a handle that was just written), ERROR_INVALID_FUNCTION,
        /// ERROR_NOT_SUPPORTED. fsync/F_FULLFSYNC: EINVAL, ENOTSUP/EOPNOTSUPP, EROFS, reported by
        /// <see cref="NativeFileSync"/> for file handles (released .NET runtimes lose Unix sync errors)
        /// and as a raw-errno HResult by other Unix streams.
        /// </summary>
        private static bool IsDurableFlushUnsupported(Exception ex)
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
