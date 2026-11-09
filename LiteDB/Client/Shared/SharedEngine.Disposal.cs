using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private System.Threading.Thread _disposeAfterCall;
        private int _retainedReads;

        // A streamed retained reader executes user transforms after Query returned.
        // Count the complete Read just like a public operation, under the same gate
        // that orders holder teardown. Its existing native hold protects storage.
        private bool ReadRetained(IBsonDataReader reader, SharedMutexPin use, int generation) => this.Call(() =>
        {
            lock (_useLock)
            {
                if ((use != null && !ReferenceEquals(_pin, use)) ||
                    (use == null && generation != _owner.Generation))
                    throw new System.ObjectDisposedException(nameof(SharedDataReader));
                this.AdmitLocked();
                _retainedReads++;
            }
            try { return reader.Read(); }
            finally
            {
                try
                {
                    // Reconcile while this read still excludes holder teardown. A
                    // stopped pin must drain after the read admission is returned;
                    // waiting here would wait for our own finally to finish.
                    this.ReconcileTransaction(waitForStopped: false);
                }
                finally
                {
                    lock (_useLock)
                    {
                        _retainedReads--;
                        System.Threading.Monitor.PulseAll(_useLock);
                    }
                }
            }
        });

        // All admitted operations have drained. Native ownership and the facade's
        // participant edge survive core close, including its publication callbacks.
        private void DisposeOwnedResources()
        {
            var closed = false;
            try
            {
                using (this.OwnershipFrame(() => _owner.IsHeld))
                lock (_useLock)
                {
                    var engine = _engine;
                    _engine = null;
                    try
                    {
                        if (engine != null)
                        {
                            closed = true;
                            this.CloseRetainedCore(engine, final: true);
                        }
                    }
                    finally
                    {
                        try { this.CloseMutexSnapshotsLocked(); }
                        finally { _databaseUsers = 0; }
                    }
                }
            }
            finally
            {
                try
                {
                    this.EndTransaction(releaseHold: false);
                    _owner.ReleaseAll();
                    if (!closed) this.CheckpointOnDispose();
                }
                finally
                {
                    try { _handles?.Dispose(); }
                    finally
                    {
                        try { _readers.Dispose(); }
                        finally
                        {
                            _owner.WaitForRelease();
                            try { this.DisposeCoordination(); }
                            finally { _admission?.Dispose(); }
                        }
                    }
                }
            }
        }
    }
}
