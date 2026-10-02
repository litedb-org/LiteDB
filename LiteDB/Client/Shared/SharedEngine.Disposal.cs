using System;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SharedEngine()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || Volatile.Read(ref _disposed) != 0) return;
            this.ThrowIfTeardownReentry();
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // A handle's wrapper returning after this point is disposed by its holder instead.
            try { this.DisposeConnection(); }
            finally { this.DisposeCachedTransactionChild(); }
        }

        private void DisposeConnection()
        {
            this.RetireCoordinatedReads();
            // Any thread can end a pin; its holder closes the engine and releases. Read
            // under the lock that orders a starting pin's publication with this Dispose.
            SharedMutexPin pin;
            lock (_useLock) pin = _pin;
            if (pin != null)
            {
                pin.RequestRelease(force: true);
                if (!pin.CanWaitFrom(Thread.CurrentThread))
                {
                    // The pin's holder still closes its engine; its streams close on return.
                    _handles?.Dispose();
                    _readers.Dispose();
                    return;
                }
                pin.WaitReleased();
            }

            // Calls admitted before Dispose started finish first; later ones are refused.
            this.WaitForAdmittedCalls();
            var closed = false;
            // The ownership an open engine implies ends only after ReleaseAll below.
            using (this.OwnershipFrame(() => _owner.IsHeld))
            lock (_useLock)
            {
                if (_engine != null)
                {
                    this.CloseRetainedCore(_engine, final: true);
                    _engine = null;
                    closed = true;
                }
                this.CloseMutexSnapshotsLocked();
                _databaseUsers = 0;
            }
            // Open readers and transactions of any thread end with the connection.
            _owner.ReleaseAll();
            // Operations left a WAL below the close threshold: checkpoint it now, so
            // the data file alone is the database again once every connection closed.
            if (!closed && !_transactionChild) this.CheckpointOnDispose();
            _handles?.Dispose();
            // Leased readers may outlive the connection; the slot file closes after the last.
            _readers.Dispose();
            // A disposed connection holds no mutex, even for the moment its holder
            // needs to release it; another connection's final close may try it next.
            _owner.WaitForRelease();
            this.DisposeCoordination();
        }
    }
}
