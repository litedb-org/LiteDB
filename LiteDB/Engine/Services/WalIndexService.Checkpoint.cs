using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        private readonly Dictionary<int, int> _snapshots = new Dictionary<int, int>();
        private readonly Func<int[]> _sharedReaders;
        private int _backfillVersion;
        private readonly Dictionary<int, long> _confirmationPositions = new Dictionary<int, long>();

#if DEBUG || TESTING
        internal Action SnapshotCaptured;
        internal int SnapshotCount => _snapshots.Values.Sum();
        internal int BackfillVersion => _backfillVersion;
#endif

        internal int PinSnapshot()
        {
            _indexLock.EnterWriteLock();
            try
            {
                var version = _currentReadVersion;
#if DEBUG || TESTING
                SnapshotCaptured?.Invoke();
#endif
                _snapshots.TryGetValue(version, out var count);
                _snapshots[version] = count + 1;
                return version;
            }
            finally { _indexLock.ExitWriteLock(); }
        }

        internal void UnpinSnapshot(int version)
        {
            _indexLock.EnterWriteLock();
            try
            {
                if (--_snapshots[version] == 0) _snapshots.Remove(version);
            }
            finally { _indexLock.ExitWriteLock(); }
        }

        /// <summary>
        /// Ascending snapshot versions of this process and of shared readers.
        /// The caller owns the index write lock.
        /// </summary>
        private int[] LiveVersions(int[] shared)
        {
            return _snapshots.Keys.Concat(shared).Distinct().OrderBy(version => version).ToArray();
        }

        public int Checkpoint() => this.TryCheckpoint(rationed: false);

        public int TryCheckpoint() => this.TryCheckpoint(rationed: false);

        public int TryAutoCheckpoint() => this.TryCheckpoint(rationed: true);

        /// <summary>
        /// Checkpoint on engine close. A close that can reclaim always runs; shared
        /// engines ration partial work under live leases like commits do.
        /// </summary>
        public int TryCloseCheckpoint() => this.TryCheckpoint(rationed: _rationClose);

        /// <summary>
        /// Backfill only committed versions visible to every snapshot. Frames that
        /// no live or future snapshot can resolve are cleared and become reusable.
        /// </summary>
        private int TryCheckpoint(bool rationed)
        {
            try { return TryCheckpointCore(rationed); }
            catch (Exception error)
            {
                _disk.StopAfterCheckpointFailure(error);
                throw;
            }
        }

        private int TryCheckpointCore(bool rationed)
        {
            if (_disk.GetFileLength(FileOrigin.Log) == 0) return 0;

            // Acquire transaction exclusion before the index lock. Snapshot disposal
            // needs the index lock, so waiting for transactions while holding it deadlocks.
            var wait = !rationed || _backoff.TryClaimWaitingAttempt();
            var timeout = wait ? READER_WAIT_MILLISECONDS : NO_WAIT_MILLISECONDS;
            var exclusive = _locker.TryEnterExclusive(out var mustExit, waitForReaders: wait, milliseconds: timeout);
            // Partial work under readers costs a WAL scan and several syncs.
            // A commit only pays for it on the same back-off as the waiting attempt.
            if (!exclusive && !wait) return 0;
            var indexEntered = false;
            var commitEntered = false;
            var writerEntered = false;
            var structural = false;
            object commitLock = null;
            try
            {
                commitLock = _getCommitLock();
                // A coordinator's clients open snapshots without a lock: tell them before
                // the lease scan, so a snapshot registered after it is never accepted.
                if (_signals != null)
                {
                    _signals.StructuralBegin();
                    structural = true;
                }
                // Scanning lease files is filesystem work; keep it outside the index
                // lock. The database mutex already orders it with lease registration.
                var shared = _sharedReaders == null ? new int[0] : _sharedReaders();
                // Null means the external reader registry could not be inspected.
                // Neither backfill nor reclamation is safe without the oldest
                // snapshot version, so leave the WAL untouched and retry later.
                if (shared == null) return 0;
                _disk.CheckpointStage("before-commit-lock");
                System.Threading.Monitor.Enter(commitLock, ref commitEntered);
                _disk.CheckpointStage("before-index-lock");
                _indexLock.EnterWriteLock();
                indexEntered = true;
                System.Threading.Monitor.Enter(_disk.WalWriterLock, ref writerEntered);
                var live = this.LiveVersions(shared);
                var target = live.Length == 0 ? _currentReadVersion : live[0];
                var reclaim = exclusive && live.Length == 0;
                // Exclusion alone does not mean readers are gone: snapshots and
                // shared leases survive it. Only a reclaiming checkpoint ends the
                // back-off; otherwise an unclaimed commit skips partial work, which
                // starts with a full WAL validation.
                if (reclaim) _backoff.Reset();
                else if (!wait) return 0;
                this.ValidateCheckpoint();

                var pages = new List<PagePosition>();
                foreach (var entry in _index)
                {
                    var version = entry.Value.LastOrDefault(item => item.Key <= target);
                    if (version.Key > _backfillVersion)
                    {
                        pages.Add(new PagePosition(entry.Key, version.Value));
                    }
                }

                var obsolete = reclaim ? new List<long>() : this.FindObsoleteFrames(live);
                if (pages.Count == 0 && obsolete.Count == 0 && !reclaim) return 0;

                // WAL must be durable before its pages can reach the data file.
                // The data flush completes before truncation can become durable.
                var retirement = _disk.PrepareRetirement(obsolete);
                _disk.SyncLogBeforeCheckpoint();
                _disk.WriteDataDisk(_disk.ReadCheckpointPages(pages));
                _backfillVersion = target;

                if (!reclaim) _disk.CompletePartialCheckpoint(retirement);

                if (obsolete.Count > 0)
                {
                    _disk.ReclaimLogPages(obsolete);
                }

                if (reclaim)
                {
                    // Clear takes the same non-recursive lock; perform its steps here.
                    _disk.ClearSchemaCache();
                    _disk.Cache.Clear();
                    _disk.CheckpointStage("before-reclaim");
                    _disk.RotateWalSalt();
#if DEBUG || TESTING
                    _disk.TestCrashPoint("checkpoint-before-clear");
#endif
                    _disk.SetLength(0, FileOrigin.Log);
#if DEBUG || TESTING
                    _disk.TestCrashPoint("checkpoint-after-clear");
#endif
                    _disk.CheckpointStage("after-reclaim");
                    _confirmationPositions.Clear();
                    _confirmTransactions.Clear();
                    _index.Clear();
                    _lastTransactionID = 0;
                    _currentReadVersion = 0;
                    _backfillVersion = 0;
                }
                return pages.Count;
            }
            finally
            {
                if (writerEntered) System.Threading.Monitor.Exit(_disk.WalWriterLock);
                if (indexEntered) _indexLock.ExitWriteLock();
                if (commitEntered) System.Threading.Monitor.Exit(commitLock);
                if (mustExit) _locker.ExitExclusive();
                if (structural) _signals.StructuralEnd(_currentReadVersion);
            }
        }
    }
}
