using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
            this.ValidatePublicDispatch();
            _state.Validate();

            if (_settings.ReadOnly) throw new IOException("Cannot start a transaction in a read-only database.");

            var transacion = _monitor.GetTransaction(true, false, out var isNew);

            if (transacion.OpenCursors.Count > 0) throw new LiteException(0, "This thread contains an open cursors/query. Close cursors before Begin()");

            if (isNew) transacion.ExplicitTransaction = true;

            _monitor.ConsumeExplicitAbort();

            LOG(isNew, $"begin trans", "COMMAND");

            return isNew;
        }

        /// <summary>
        /// Persist all dirty pages into LOG file
        /// </summary>
        public bool Commit()
        {
            this.ValidatePublicDispatch();
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
        public bool Rollback() => this.Rollback(cleanup: false);

        /// <summary>
        /// Roll back a disposed handle's transaction. If another failure already stopped the
        /// engine, its owner performs that cleanup; disposal must not rethrow a peer's error.
        /// </summary>
        internal bool RollbackHandleOnDispose() => this.Rollback(cleanup: true);

        private bool Rollback(bool cleanup)
        {
            this.ValidatePublicDispatch();
            if (cleanup)
            {
                if (_state.IsUnavailable) return false;
            }
            else _state.Validate();

            var transaction = this.GetTransactionForCompletion(commit: false);

            if (transaction != null && transaction.State == TransactionState.Active)
            {
                this.RollbackAndReleaseTransaction(transaction, cleanup);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Create (or reuse) a transaction an add try/catch block. Commit transaction if is new transaction
        /// </summary>
        private T AutoTransaction<T>(Func<TransactionService, T> fn) => this.ExecuteAutoTransaction(fn, true);

        private T AutoReadTransaction<T>(Func<TransactionService, T> fn) => this.ExecuteAutoTransaction(fn, false);

        private T ExecuteAutoTransaction<T>(Func<TransactionService, T> fn, bool write)
        {
            _state.Validate();

            if (write && _settings.ReadOnly) throw new ReadOnlyRefusalException("Cannot modify a read-only database.");

            var transaction = _monitor.GetTransaction(true, false, out var isNew);

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
                if (_state.Handle(ex) && transaction.State == TransactionState.Active)
                {
                    if (transaction.Owner.Explicit == null)
                    {
                        this.RollbackAndReleaseTransaction(transaction);

                        if (transaction.ExplicitTransaction) _monitor.MarkExplicitAbort();
                    }
                    else
                    {
                        // A handle's statement error stays primary; a cleanup failure is attached.
                        try { this.RollbackAndReleaseTransaction(transaction); }
                        catch (Exception cleanup) { ex.Data["LiteDB.StatementRollback"] = cleanup; }
                        finally { _monitor.MarkExplicitAbort(); }
                    }
                }

                throw;
            }
        }

        private void CommitAndReleaseTransaction(TransactionService transaction)
        {
            try
            {
                transaction.Commit();
                _monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                // Completion may have partially persisted state. Do not let a later
                // write reuse this transaction and report success without committing.
                _state.Stop(ex);
                throw;
            }

            // try checkpoint when finish transaction and log file are bigger than checkpoint pragma value (in pages)
            if (_header.Pragmas.Checkpoint > 0 &&
                _disk.GetFileLength(FileOrigin.Log) >= (_header.Pragmas.Checkpoint * PAGE_SIZE))
            {
                _walIndex.TryAutoCheckpoint();
            }
        }

        private void RollbackAndReleaseTransaction(TransactionService transaction, bool cleanup = false)
        {
            try
            {
                transaction.Rollback();
                _monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                // A peer can publish failure after the initial availability check. Compare
                // before Stop: Stop may publish this operation's own error unchanged.
                if (cleanup && _state.IsPublishedFailure(ex)) return;
                _state.Stop(ex);
                throw;
            }
        }
    }
}
