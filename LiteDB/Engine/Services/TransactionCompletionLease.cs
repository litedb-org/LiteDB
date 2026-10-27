using System;
using System.Threading;

namespace LiteDB.Engine
{
    // The opted-in close path can retire transactions from another thread after
    // draining WAL I/O. Snapshot completion must finish before that retirement.
    // A null gate preserves the default path without allocating a lock object.
    internal readonly struct TransactionCompletionLease : IDisposable
    {
        private readonly object _gate;

        internal TransactionCompletionLease(object gate)
        {
            _gate = gate;
            if (gate != null) Monitor.Enter(gate);
        }

        public void Dispose()
        {
            if (_gate != null) Monitor.Exit(_gate);
        }
    }
}
