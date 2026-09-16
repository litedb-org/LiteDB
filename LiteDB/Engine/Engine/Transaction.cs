using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Initialize a new transaction. Transaction are created "per-thread". There is only one single transaction per thread.
        /// Return true when created; false joins the current thread transaction. Keep the block synchronous, with no await.
        /// </summary>
        public bool BeginTrans()
        {
            var monitor = this.CaptureTransactionMonitor(out _);
            var transacion = monitor.GetTransaction(true, false, out var isNew);

            if (transacion.OpenCursors.Count > 0) throw new LiteException(0, "This thread contains an open cursors/query. Close cursors before Begin()");

            if (isNew) transacion.ExplicitTransaction = true;

            monitor.ConsumeExplicitAbort();

            LOG(isNew, $"begin trans", "COMMAND");

            return isNew;
        }

        /// <summary>
        /// Persist all dirty pages into LOG file
        /// </summary>
        public bool Commit()
        {
            _state.Validate();

            var transaction = this.GetTransactionForCompletion(commit: true);

            if (transaction != null)
            {
                // do not accept explicit commit transaction when contains open cursors running
                if (transaction.OpenCursors.Count > 0) throw new LiteException(0, "Current transaction contains open cursors. Close cursors before run Commit()");

                if (transaction.State == TransactionState.Active)
                {
                    this.CommitAndReleaseTransaction(transaction);

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Do rollback to current transaction. Clear dirty pages in memory and return new pages to main empty linked-list
        /// </summary>
        public bool Rollback()
        {
            _state.Validate();

            var transaction = this.GetTransactionForCompletion(commit: false);

            if (transaction != null && transaction.State == TransactionState.Active)
            {
                this.RollbackAndReleaseTransaction(transaction);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Create (or reuse) a transaction an add try/catch block. Commit transaction if is new transaction
        /// </summary>
        private T AutoTransaction<T>(Func<TransactionService, T> fn)
        {
            var monitor = this.CaptureTransactionMonitor(out var state);
            var transaction = monitor.GetTransaction(true, false, out var isNew);

            try
            {
                var result = fn(transaction);

                // if this transaction was auto-created for this operation, commit & dispose now
                if (isNew)
                    this.CommitAndReleaseTransaction(transaction);

                return result;
            }
            catch(Exception ex)
            {
                if (state.Handle(ex) && transaction.State == TransactionState.Active)
                {
                    this.RollbackAndReleaseTransaction(transaction);

                    if (transaction.ExplicitTransaction) monitor.MarkExplicitAbort();
                }

                throw;
            }
        }

        private TransactionMonitor CaptureTransactionMonitor(out EngineState state)
        {
            lock (_lifecycleLock)
            {
                state = _state;
                state.Validate();
                // Register outside this lock: a waiting checkpoint must be able
                // to finish existing transactions. If rebuild wins before the
                // registration, this generation's disposed monitor rejects it.
                return _monitor;
            }
        }

        private void CommitAndReleaseTransaction(TransactionService transaction)
        {
            EngineState state;
            TransactionMonitor monitor;
            lock (_lifecycleLock)
            {
                state = _state;
                monitor = _monitor;
            }

            // An open transaction holds the transaction gate, so rebuild cannot replace
            // this generation before it is released. Commit outside the lifecycle lock:
            // its durable flush (#2818) must not stall readers that capture the monitor.
            try
            {
                transaction.Commit();
                monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                // Completion may have partially persisted state. Do not let a later
                // write reuse this transaction and report success without committing.
                state.Stop(ex);
                throw;
            }

            lock (_lifecycleLock)
            {
                // Once released, rebuild or close may have replaced this generation.
                if (!ReferenceEquals(state, _state) || state.Disposed) return;

                try
                {
                    if (_header.Pragmas.Checkpoint > 0 &&
                        _disk.GetFileLength(FileOrigin.Log) >= (_header.Pragmas.Checkpoint * PAGE_SIZE))
                        _walIndex.TryAutoCheckpoint();
                }
                catch (Exception ex)
                {
                    // Explicit Commit needs the same critical-I/O handling as
                    // auto-transactions. Pre-checkpoint access errors can retry.
                    _state.Handle(ex);
                    throw;
                }
            }
        }

        private void RollbackAndReleaseTransaction(TransactionService transaction)
        {
            lock (_lifecycleLock)
            {
                try
                {
                    transaction.Rollback();
                    _monitor.ReleaseTransaction(transaction);
                }
                catch (Exception ex)
                {
                    _state.Stop(ex);
                    throw;
                }
            }
        }
    }
}
