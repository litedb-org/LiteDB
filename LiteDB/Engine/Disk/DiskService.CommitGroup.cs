using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private WalCommitGroup _commitGroup;
        internal int CommitReadVersionLimit => Volatile.Read(ref _commitGroup)?.ReadVersionLimit ?? int.MaxValue;
        internal IEnumerable<int> PendingCommitVersions => Volatile.Read(ref _commitGroup)?.PendingVersions ?? System.Linq.Enumerable.Empty<int>();

        // Snapshot capture must not initialize the lazy writer: an open failure
        // would occur before its pin was registered. Any published grouped version
        // already has a group, initialized before append and index confirmation.
        internal void CaptureCommitDependency(TransactionPages pages) =>
            Volatile.Read(ref _commitGroup)?.CaptureDependency(pages);

        internal WalCommitGroup CommitGroup
        {
            get
            {
#if NET8_0_OR_GREATER
                if (_durableCommits && ChecksumsEnabled && (_signals == null || _durableCommitPublication != null) && !_logFlushDegraded &&
                    !LogSyncUnverified && ((ChecksummedWalFactory)_logFactory).IsFile &&
                    ((ChecksummedWalStream)_writer.Value).RawStream.GetType() == typeof(FileStream))
                {
                    var group = Volatile.Read(ref _commitGroup);
                    if (group == null)
                    {
                        group = new WalCommitGroup(this, _state);
                        group = Interlocked.CompareExchange(ref _commitGroup, group, null) ?? group;
                    }
                    return group;
                }
#endif
                return null;
            }
        }

        // Caller owns the writer monitor: exposing SafeFileHandle flushes buffers.
        internal Microsoft.Win32.SafeHandles.SafeFileHandle CaptureCommitHandle(out string path)
        {
            var file = (FileStream)((ChecksummedWalStream)_writer.Value).RawStream;
            path = file.Name;
            return file.SafeFileHandle;
        }

        internal void FlushCommitGroup(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
        {
            try
            {
                NativeFileSync.FlushDevice(handle, path);
                _logSyncProven = true;
            }
            catch (System.Exception error) when (IsDurableFlushUnsupported(error))
            {
                MarkLogFlushDegraded(error);
            }
            // A concurrent uncertain append may have stopped the engine during the
            // descriptor sync. Do not start a new directory barrier on that instance.
            _state.Validate();
            SyncLogDirectory();
        }
    }
}
