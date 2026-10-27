using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Appends are ordered by the header/writer monitors. Device barriers are
    /// serialized separately and cover exactly the ticket captured before sync.
    /// No thread waits to collect a batch: the first committer syncs immediately.
    /// </summary>
    internal sealed class WalCommitGroup
    {
        private readonly DiskService _disk;
        private readonly EngineState _state;
        private readonly object _sync = new object();
        private long _appended;
        private long _durable;
        private int _appendedVersion;
        private int _readVersionLimit = int.MaxValue;
        private readonly List<int> _pendingVersions = new List<int>();

        internal WalCommitGroup(DiskService disk, EngineState state)
        {
            _disk = disk;
            _state = state;
        }

        internal int ReadVersionLimit => Volatile.Read(ref _readVersionLimit);
        // Checkpoint owns the WAL writer monitor. Every pending barrier can expose
        // a prefix, so retaining only the current reader limit would be insufficient.
        internal IEnumerable<int> PendingVersions => _pendingVersions;

        // Caller holds the WAL writer monitor, before WAL-index publication.
        internal long Append(int previousVersion, int version)
        {
            if (_appended == _durable) Volatile.Write(ref _readVersionLimit, previousVersion);
            _appendedVersion = version;
            _pendingVersions.Add(version);
            return ++_appended;
        }

        // Snapshot capture owns the index lock. Taking the writer monitor follows
        // checkpoint's lock order and includes every appended version it can see.
        // A later, not yet published append may be included conservatively.
        internal void CaptureDependency(TransactionPages pages)
        {
            lock (_disk.WalWriterLock)
            {
                pages.CommitTicket = Math.Max(pages.CommitTicket, _appended);
            }
        }

        internal bool TryCommit(TransactionService transaction, HeaderPage header)
        {
            if (transaction.Pages.HasCommitCallbacks || transaction.OpenCursors.Count != 0) return false;
            lock (header)
            {
                transaction.Pages.DeferCommitSync = true;
                transaction.PersistAndConfirm();
                // Writers may build on the appended version. Readers are pinned
                // at ReadVersionLimit until a successful sync covers that version.
                transaction.ReleaseCommitSnapshots();
            }
            return true;
        }

        internal void EnsureDurable(long ticket)
        {
            Exception failure = null;
            var ownsFailure = false;
            lock (_sync)
            {
#if DEBUG || TESTING
                if (_disk.CheckpointBarrier != null) _disk.TestCrashPoint("wal-before-checkpoint-barrier");
#endif
                using var barrier = _disk.CheckpointBarrier?.Enter() ?? default;
                try
                {
                    _state.Validate();
                    if (_durable >= ticket) return;
                    long target;
                    int version;
                    Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                    string path;
                    lock (_disk.WalWriterLock)
                    {
                        _state.Validate();
                        target = _appended;
                        version = _appendedVersion;
                        try
                        {
#if DEBUG || TESTING
                            _disk.TestCrashPoint("wal-before-batch-flush");
#endif
                            ((Stream)_disk.WalWriterLock).Flush();
                            // The SafeFileHandle getter itself flushes FileStream buffers.
                            // Capture it while writes are excluded, never during the sync.
                            handle = _disk.CaptureCommitHandle(out path);
                        }
                        catch (Exception error)
                        {
                            // Padding or a buffered write can tear here. Publish the stop
                            // before releasing the writer, as for WriteLogDisk's uncertain batch.
                            failure = AsWriteFailure(error);
                            ownsFailure = BeginStop(failure);
                            throw failure;
                        }
                    }
#if DEBUG || TESTING
                    _disk.TestCrashPoint("wal-before-durable-flush");
#endif
                    // Another appender can have stopped the engine since target capture.
                    _state.Validate();
                    _disk.FlushCommitGroup(handle, path);
#if DEBUG || TESTING
                    _disk.TestCrashPoint("wal-after-durable-flush");
#endif
                    lock (_disk.WalWriterLock)
                    {
                        _state.Validate();
                        _durable = target;
                        var remove = 0;
                        while (remove < _pendingVersions.Count && _pendingVersions[remove] <= version) remove++;
                        _pendingVersions.RemoveRange(0, remove);
                        Volatile.Write(ref _readVersionLimit, target == _appended ? int.MaxValue : version);
                    }
                }
                catch (Exception error)
                {
                    // Publish before another waiter can become leader. Teardown
                    // runs below, outside both monitors, as for ordinary commits.
                    if (failure == null)
                    {
                        failure = AsWriteFailure(error);
                        lock (_disk.WalWriterLock) ownsFailure = BeginStop(failure);
                    }
                }
            }
            if (failure != null)
            {
                _state.CompleteStop(failure, ownsFailure);
                throw failure;
            }
        }

        private static IOException AsWriteFailure(Exception error) =>
            error as IOException ?? new IOException("WAL durable flush failed.", error);

        // Caller holds the writer monitor. Record the causal failure before making
        // the engine terminal; later waiters must preserve that first diagnostic.
        private bool BeginStop(Exception failure)
        {
            if (!_state.Stopped && !_state.Disposed)
                _disk.RecordWriteFailure("A grouped WAL flush", WriteFailure.InFile(failure, FileOrigin.Log));
            return _state.BeginStop(failure);
        }
    }
}
