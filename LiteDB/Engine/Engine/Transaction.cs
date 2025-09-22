using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Initialize a new transaction. Transaction are created "per-thread". There is only one single transaction per thread.
        /// Return true if transaction was created or false if current thread already in a transaction.
        /// </summary>
        public Task<bool> BeginTransAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _state.Validate();

            var transacion = _monitor.GetTransaction(true, false, out var isNew);

            transacion.ExplicitTransaction = true;

            if (transacion.OpenCursors.Count > 0) throw new LiteException(0, "This thread contains an open cursors/query. Close cursors before Begin()");

            LOG(isNew, $"begin trans", "COMMAND");

            return Task.FromResult(isNew);
        }

        /// <summary>
        /// Persist all dirty pages into LOG file
        /// </summary>
        public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _state.Validate();

            var transaction = _monitor.GetTransaction(false, false, out _);

            if (transaction != null)
            {
                // do not accept explicit commit transaction when contains open cursors running
                if (transaction.OpenCursors.Count > 0) throw new LiteException(0, "Current transaction contains open cursors. Close cursors before run Commit()");

                if (transaction.State == TransactionState.Active)
                {
                    await this.CommitAndReleaseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Do rollback to current transaction. Clear dirty pages in memory and return new pages to main empty linked-list
        /// </summary>
        public Task<bool> RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _state.Validate();

            var transaction = _monitor.GetTransaction(false, false, out _);

            if (transaction != null && transaction.State == TransactionState.Active)
            {
                transaction.Rollback();

                _monitor.ReleaseTransaction(transaction);

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// Create (or reuse) a transaction an add try/catch block. Commit transaction if is new transaction
        /// </summary>
        private async Task<T> AutoTransactionAsync<T>(Func<TransactionService, CancellationToken, ValueTask<T>> fn, CancellationToken cancellationToken)
        {
            _state.Validate();

            var transaction = _monitor.GetTransaction(true, false, out var isNew);

            try
            {
                var result = await fn(transaction, cancellationToken).ConfigureAwait(false);

                // if this transaction was auto-created for this operation, commit & dispose now
                if (isNew)
                {
                    await this.CommitAndReleaseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch(Exception ex)
            {
                if (_state.Handle(ex))
                {
                    transaction.Rollback();

                    _monitor.ReleaseTransaction(transaction);
                }

                throw;
            }
        }

        private ValueTask CommitAndReleaseTransactionAsync(TransactionService transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            transaction.Commit();

            _monitor.ReleaseTransaction(transaction);

            // try checkpoint when finish transaction and log file are bigger than checkpoint pragma value (in pages)
            if (_header.Pragmas.Checkpoint > 0 &&
                _disk.GetFileLength(FileOrigin.Log) > (_header.Pragmas.Checkpoint * PAGE_SIZE))
            {
                _walIndex.TryCheckpoint();
            }

            return default;
        }
    }
}