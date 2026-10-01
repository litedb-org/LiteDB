using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// The explicit transaction handles of one database. Closing the database refuses new
    /// handles, waits for begins already admitted, and rolls back every handle that is still
    /// active before the engine is released. Completed handles unregister immediately.
    /// </summary>
    internal sealed class TransactionHandles
    {
        private readonly object _gate = new object();
        private readonly HashSet<LiteTransaction> _active = new HashSet<LiteTransaction>();
        private int _pending;
        private bool _closed;

#if DEBUG || TESTING
        internal int ActiveCount { get { lock (_gate) return _active.Count; } }
#endif

        internal void ThrowIfClosed()
        {
            lock (_gate) if (_closed) throw new ObjectDisposedException(nameof(LiteDatabase));
        }

        /// <summary>Admit one begin; it must end with <see cref="Register"/> or <see cref="Abandon"/>.</summary>
        internal void EnterPending()
        {
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(LiteDatabase));
                _pending++;
            }
        }

        internal void Abandon()
        {
            lock (_gate)
            {
                _pending--;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Publish an admitted handle. A closing database rolls it back like the others.</summary>
        internal void Register(LiteTransaction transaction)
        {
            lock (_gate)
            {
                _pending--;
                _active.Add(transaction);
                Monitor.PulseAll(_gate);
            }
        }

        internal void Completed(LiteTransaction transaction)
        {
            lock (_gate) _active.Remove(transaction);
        }

        /// <summary>
        /// Settle every handle. A call executing on another thread finishes first; closing from
        /// inside an executing handle call is rejected before any state changes.
        /// </summary>
        internal void ThrowIfClosingFromHandleCall()
        {
            lock (_gate)
            {
                if (_active.Any(transaction => transaction.IsExecutingOnCurrentThread))
                    throw new InvalidOperationException("Cannot close a database from inside its executing transaction handle operation.");
            }
        }

        internal void Close()
        {
            this.ThrowIfClosingFromHandleCall();
            lock (_gate) _closed = true;

            List<Exception> errors = null;
            while (true)
            {
                LiteTransaction[] active;
                lock (_gate)
                {
                    while (_active.Count == 0 && _pending != 0) Monitor.Wait(_gate);
                    if (_active.Count == 0) break;
                    active = _active.ToArray();
                }
                // An executing call may wait for a lock of an idle handle: once every handle
                // refuses new calls, roll back the idle ones before waiting for executing ones.
                foreach (var transaction in active) transaction.RefuseNewCalls();
                foreach (var transaction in active.Where(t => !t.IsExecuting).Concat(active.Where(t => t.IsExecuting)).ToArray())
                {
                    try { transaction.CloseForDatabase(); }
                    catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
                    finally { this.Completed(transaction); }
                }
            }

            if (errors == null) return;
            for (var i = 1; i < errors.Count; i++) errors[0].Data["LiteDB.TransactionCloseCleanup." + i] = errors[i];
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
    }
}
