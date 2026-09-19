using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        private readonly Dictionary<int, int> _snapshots = new Dictionary<int, int>();
        private readonly Func<int?> _oldestReader;
        private int _backfillVersion;

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

        public int Checkpoint() => this.TryCheckpoint();

        /// <summary>
        /// Backfill only committed versions visible to every snapshot. Keep the
        /// complete WAL generation until both local transactions and shared
        /// reader leases have drained: even a resolved but unread offset stays valid.
        /// </summary>
        public int TryCheckpoint()
        {
            // Acquire transaction exclusion before the index lock. Snapshot disposal
            // needs the index lock, so waiting for transactions while holding it deadlocks.
            var exclusive = _locker.TryEnterExclusive(out var mustExit);
            _indexLock.EnterWriteLock();
            try
            {
                if (_disk.GetFileLength(FileOrigin.Log) == 0) return 0;
                var external = _oldestReader?.Invoke();
                var target = _snapshots.Count == 0 ? _currentReadVersion : _snapshots.Keys.Min();
                if (external.HasValue) target = Math.Min(target, external.Value);
                var reclaim = exclusive && _snapshots.Count == 0 && !external.HasValue;

                var pages = new List<PagePosition>();
                foreach (var entry in _index)
                {
                    var version = entry.Value.LastOrDefault(item => item.Key <= target);
                    if (version.Key > _backfillVersion)
                    {
                        pages.Add(new PagePosition(entry.Key, version.Value));
                    }
                }

                if (pages.Count == 0 && !reclaim) return 0;

                // WAL must be durable before its pages can reach the data file.
                // The data flush completes before truncation can become durable.
                _disk.FlushLog();
                _disk.WriteDataDisk(_disk.ReadCheckpointPages(pages));
                _backfillVersion = target;

                if (reclaim)
                {
                    // Clear takes the same non-recursive lock; perform its steps here.
                    _disk.Cache.Clear();
                    _disk.CheckpointStage("before-reclaim");
                    _disk.SetLength(0, FileOrigin.Log);
                    _disk.CheckpointStage("after-reclaim");
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
                _indexLock.ExitWriteLock();
                if (mustExit) _locker.ExitExclusive();
            }
        }
    }
}
