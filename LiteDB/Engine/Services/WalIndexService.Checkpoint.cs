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

        /// <summary>
        /// Backfill every frame and empty the WAL, or change nothing and return false: when a
        /// snapshot of this or another process may still read the WAL, or the shared reader
        /// registry cannot be inspected.
        /// </summary>
        public bool TryDrain()
        {
            this.TryCheckpoint(rationed: false, drain: true);
            return _disk.GetFileLength(FileOrigin.Log) == 0;
        }

        /// <summary>
        /// Whether a drain would change nothing now because another connection may read the WAL:
        /// it is not empty and a shared reader holds a snapshot, or the registry cannot be inspected.
        /// </summary>
        public bool DrainBlocked()
        {
            if (_disk.GetFileLength(FileOrigin.Log) == 0) return false;
            var shared = _sharedReaders == null ? new int[0] : _sharedReaders();
            return shared == null || shared.Length > 0;
        }

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
        private int TryCheckpoint(bool rationed, bool drain = false)
        {
            var stopBegun = false;
            var stopOwned = false;
            try { return TryCheckpointCore(rationed, drain, ref stopBegun, ref stopOwned); }
            catch (Exception error)
            {
                _disk.StopAfterCheckpointFailure(error, stopBegun, stopOwned);
                throw;
            }
        }

        private int TryCheckpointCore(bool rationed, bool drain, ref bool stopBegun, ref bool stopOwned)
        {
            if (_disk.GetFileLength(FileOrigin.Log) == 0) return 0;

            // The WAL is kept until a data sync succeeds (DiskService.KeepsWal). Once this engine or, in
            // shared mode, an earlier engine of the connection found that the data file cannot sync, a
            // checkpoint retries the data sync first and changes nothing while it still fails, instead
            // of scanning a WAL that grows at every commit.
            if (_disk.DefersCheckpoint()) return 0;

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
                if (drain && !reclaim) return 0;
                // A drain (legacy conversion) runs before the open trims partial pages. Trim them
                // now, before this checkpoint appends its header journal at the physical WAL end:
                // a torn legacy frame completed by journal bytes would pass as a committed page.
                if (drain) _disk.TrimTrailingPages();
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

                // Retiring frames only lets their slots be reused, which storage that cannot
                // sync never does; its witness could not be published durably either.
                var obsolete = reclaim ? new List<long>() : this.FindObsoleteFrames(live);
                var proven = obsolete.Count > 0 && _disk.ProveRetirementSyncs();
                if (!proven) obsolete.Clear();
                if (pages.Count == 0 && obsolete.Count == 0 && !reclaim) return 0;

                // Write only to a data file that just synced (the retirement proof syncs it too): one
                // that cannot, also one another engine or process found so, keeps the WAL untouched.
                if (!proven && !_disk.LogIsVolatile && !_disk.DataFileSyncs()) return 0;

                // WAL must be durable before its pages can reach the data file.
                // The data flush completes before truncation can become durable.
                var retirement = _disk.PrepareRetirement(obsolete);
                // A log that cannot sync backs no overwrite (decision D): write nothing, keep the WAL.
                if (!_disk.SyncLogBeforeCheckpoint()) return 0;
                _disk.WriteDataDisk(_disk.ReadCheckpointPages(pages));
                _backfillVersion = target;

                // The data file stopped syncing since the sync above (#2242): the backfill may be torn
                // and the WAL and its header journal are its only durable copy. Stop before removing
                // any of it; the next open recovers from them (DiskService.KeepsWal).
                if (_disk.KeepsWal) throw DiskService.DataStoppedSyncing("a checkpoint");

                // Storage that stopped syncing after the retirement's proof may not have made its
                // witness records durable: a root published now could leave a durable header naming
                // records a power loss dropped. Keep the frames it would have retired.
                if (retirement != null && _disk.FlushDegraded)
                {
                    retirement = null;
                    obsolete.Clear();
                }

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
                    // Only once a data sync covered the backfill and the new salt (DiskService.ShrinkLog).
                    _disk.EmptyLog("a checkpoint");
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
            catch (Exception error) when (writerEntered)
            {
                // Stop before the WAL writer is released (see DiskService.BeginCheckpointStop).
                stopBegun = _disk.BeginCheckpointStop(error, out stopOwned);
                throw;
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
