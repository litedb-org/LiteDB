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
            this.EnsureOpen();

            // A write failure ended this thread's transaction: it still counts as open until Commit
            // (which throws) or Rollback completes it.
            if (this.HasLostTransaction()) return false;

            // Storage opened read-only because it cannot be written, or because its data file cannot
            // sync, accepts explicit transactions, as released versions did; writes are still rejected.
            if (_settings.ReadOnly && !_settings.ReadOnlyStorage && _settings.ReadOnlyCause == null)
                throw new IOException("Cannot start a transaction in a read-only database.");

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
            this.EnsureOpen();

            // A write failure ended this thread's transaction before it committed.
            if (this.TakeLostTransaction()) throw this.ReadOnlyWrite();

            var transaction = this.GetTransactionForCompletion(commit: true);

            if (transaction != null)
            {
                // do not accept explicit commit transaction when contains open cursors running
                if (transaction.OpenCursors.Count > 0) throw new LiteException(0, "Current transaction contains open cursors. Close cursors before run Commit()");

                if (transaction.State == TransactionState.Active)
                {
                    // A failed safepoint discarded pages this transaction changed: never publish the rest.
                    if (transaction.WriteFailure != null)
                    {
                        this.RollbackAndReleaseTransaction(transaction);
                        throw TransactionService.WriteFailed(transaction.WriteFailure);
                    }

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
            this.EnsureOpen();

            if (this.TakeLostTransaction()) return true;

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
        private T AutoTransaction<T>(Func<TransactionService, T> fn) => this.ExecuteAutoTransaction(fn, true);

        private T AutoReadTransaction<T>(Func<TransactionService, T> fn) => this.ExecuteAutoTransaction(fn, false);

        /// <summary>A write to a read-only engine; one that opened read-only on its own says why.</summary>
        private IOException ReadOnlyWrite() =>
            _settings.WriteFailure != null ? new IOException(WriteFailedPrefix + _settings.WriteFailure) :
            new IOException(_settings.ReadOnlyCause == null
                ? "Cannot modify a read-only database."
                : "Cannot modify this database: it opened read-only because the writable open was refused. " + _settings.ReadOnlyCause);

        internal const string WriteFailedPrefix = "Cannot modify this database: an earlier write failed, so the engine continues " +
            "read-only until the database is reopened. ";

        private T ExecuteAutoTransaction<T>(Func<TransactionService, T> fn, bool write)
        {
            this.EnsureOpen();

            if (write && _settings.ReadOnly) throw ReadOnlyWrite();
            if (write) this.RequireWalBelowLimit();

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
                    this.RollbackAndReleaseTransaction(transaction);

                    if (transaction.ExplicitTransaction) _monitor.MarkExplicitAbort();
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
                if (ex is IOException) _disk.RecordWriteFailure("A commit", ex);
                _state.Stop(ex);
                throw;
            }

            // try checkpoint when finish transaction and log file are bigger than checkpoint pragma value (in pages)
            if (this.CheckpointPages > 0 &&
                _disk.GetFileLength(FileOrigin.Log) >= (this.CheckpointPages * PAGE_SIZE))
            {
                // This commit succeeded: a checkpoint's write or sync failure is not its caller's
                // (decision 6). It is recorded, $database reports it, and the next write throws it.
                try { _walIndex.TryAutoCheckpoint(); }
                catch (Exception) when (_state.WriteFailure != null) { }
            }
        }

        private void RollbackAndReleaseTransaction(TransactionService transaction)
        {
            try
            {
                transaction.Rollback();
                _monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                if (ex is IOException) _disk.RecordWriteFailure("A rollback", ex);
                _state.Stop(ex);
                throw;
            }
        }
    }
}
