using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
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
