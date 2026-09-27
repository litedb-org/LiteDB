using System.Threading;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        private int _pinReleased;

        internal void ReleaseSnapshotPin()
        {
            if (Interlocked.Exchange(ref _pinReleased, 1) == 0)
            {
                _walIndex.UnpinSnapshot(_readVersion);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                this.Clear();
                _disposed = true;
                if (_mode == LockMode.Read && _collectionPage != null)
                {
                    _collectionPage.Buffer.Release();
                }
                if (_mode == LockMode.Write) _locker.ExitLock(_collectionName);
            }
            finally
            {
                this.ReleaseSnapshotPin();
            }
        }
    }
}
