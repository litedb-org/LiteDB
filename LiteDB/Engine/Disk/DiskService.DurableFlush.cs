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
        private volatile bool _logFlushDegraded;

        /// <summary>
        /// False when commits reach the OS cache only: the caller opted out
        /// (<see cref="EngineSettings.DurableCommits"/>) or the log storage rejected a durable flush.
        /// </summary>
        internal bool IsLogFlushDurable => _durableCommits && !_logFlushDegraded;

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
            }
            catch (Exception ex) when (IsDurableFlushUnsupported(ex))
            {
                // The pages were written successfully; only the sync request was refused.
                log.Flush();
                if (!_logFlushDegraded)
                {
                    _logFlushDegraded = true;
                    LOG($"log storage rejected durable flush ({ex.GetType().Name} 0x{ex.HResult:X8}); commits now flush to the OS cache only", "DISK");
                }
            }
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
        /// ERROR_NOT_SUPPORTED. fsync: EINVAL, ENOTSUP, EROFS surface as the raw errno on Unix runtimes
        /// that do not already ignore them.
        /// </summary>
        private static bool IsDurableFlushUnsupported(Exception ex)
        {
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
