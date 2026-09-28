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

            // A write failure ended this thread's transaction. Thread IDs are reused (pool threads), so
            // this begin may be unrelated: consume the mark and throw the recorded failure here, instead
            // of joining a transaction that is gone.
            if (this.TakeLostTransaction()) throw this.ReadOnlyWrite();

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
            var state = _state;
            var monitor = _monitor;

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
                        this.RollbackAndReleaseTransaction(transaction, state, monitor);
                        throw TransactionService.WriteFailed(transaction.WriteFailure);
                    }

                    this.CommitAndReleaseTransaction(transaction, state, monitor);

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
            var state = _state;
            var monitor = _monitor;

            if (this.TakeLostTransaction()) return true;

            var transaction = this.GetTransactionForCompletion(commit: false);

            if (transaction != null && transaction.State == TransactionState.Active)
            {
                this.RollbackAndReleaseTransaction(transaction, state, monitor);

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
            _settings.WriteFailure != null ? new IOException(WriteFailedPrefix + _settings.WriteFailure, _settings.WriteFailure.Cause) :
            new IOException(_settings.ReadOnlyCause == null
                ? "Cannot modify a read-only database."
                : "Cannot modify this database: it opened read-only because the writable open was refused. " + _settings.ReadOnlyCause);

        internal const string WriteFailedPrefix = "Cannot modify this database: an earlier write failed, so the engine continues " +
            "read-only until the database is reopened. ";

        private T ExecuteAutoTransaction<T>(Func<TransactionService, T> fn, bool write)
        {
            this.EnsureOpen();
            // This operation's engine: a failure stops it, never an engine a reopen replaced it with.
            var state = _state;
            var monitor = _monitor;

            if (write && _settings.ReadOnly) throw ReadOnlyWrite();
            if (write) this.RequireWalBelowLimit(state);

            var transaction = monitor.GetTransaction(true, false, out var isNew);

            try
            {
                var result = fn(transaction);

                // if this transaction was auto-created for this operation, commit & dispose now
                if (isNew)
                    this.CommitAndReleaseTransaction(transaction, state, monitor);

                return result;
            }
            catch(Exception ex)
            {
                if (state.Handle(ex) && transaction.State == TransactionState.Active)
                {
                    this.RollbackAndReleaseTransaction(transaction, state, monitor);

                    if (transaction.ExplicitTransaction) monitor.MarkExplicitAbort();
                }

                throw;
            }
        }

        /// <summary>
        /// A failure of the storage a completion wrote to: an I/O error, or a sync the storage refused
        /// (an encrypted log's preamble answers "cannot sync" with UnauthorizedAccessException). It is
        /// recorded (decision 6), so the engine reopens read-only instead of staying closed.
        /// </summary>
        private static bool IsStorageFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException;

        private void CommitAndReleaseTransaction(TransactionService transaction, EngineState state, TransactionMonitor monitor)
        {
            try
            {
                transaction.Commit();
                monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                // Completion may have partially persisted state. Do not let a later
                // write reuse this transaction and report success without committing.
                if (IsStorageFailure(ex)) state.Disk?.RecordWriteFailure("A commit", ex);
                state.Stop(ex);
                throw;
            }

            // Only this commit's engine checkpoints: after another thread's failure stopped it, or a
            // reopen replaced it, the services in place are not the ones this commit wrote through.
            if (state.Stopped || !ReferenceEquals(state, _state)) return;

            // try checkpoint when finish transaction and log file are bigger than checkpoint pragma value (in pages)
            if (this.CheckpointPages > 0 &&
                _disk.GetFileLength(FileOrigin.Log) >= (this.CheckpointPages * PAGE_SIZE))
            {
                // This commit succeeded: a checkpoint's write or sync failure is not its caller's
                // (decision 6). It is recorded on this commit's engine (captured above: a reopen assigns
                // a new state without it), $database reports it, and the next write throws it. A
                // checkpoint refused before it wrote anything (the data file cannot sync) is no failure.
                try { _walIndex.TryAutoCheckpoint(); }
                catch (Exception ex) when (state.WriteFailure != null || DiskService.IsRefusedBeforeWrite(ex)) { }
            }
        }

        private void RollbackAndReleaseTransaction(TransactionService transaction, EngineState state, TransactionMonitor monitor)
        {
            try
            {
                transaction.Rollback();
                monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                if (IsStorageFailure(ex)) state.Disk?.RecordWriteFailure("A rollback", ex);
                state.Stop(ex);
                throw;
            }
        }
    }
}
