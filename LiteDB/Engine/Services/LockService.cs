using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Lock service are collection-based locks. Lock will support any threads reading at same time. Writing operations will be
    /// locked based on collection. Eventually, write operation can change header page that has an exclusive locker for.
    /// [ThreadSafe]
    /// </summary>
    internal class LockService : IDisposable
    {
        private readonly EnginePragmas _pragmas;

        private readonly SemaphoreSlim _writerLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _readerSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _collections = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly AsyncLocal<LockScope> _scope = new AsyncLocal<LockScope>();

        private int _disposed;
        private int _readerCount;

        internal LockService(EnginePragmas pragmas)
        {
            _pragmas = pragmas;
        }

        /// <summary>
        /// Return if current logical context has an open transaction
        /// </summary>
        public bool IsInTransaction
        {
            get
            {
                var scope = _scope.Value;
                return scope != null && (scope.TransactionDepth > 0 || scope.ExclusiveDepth > 0);
            }
        }

        /// <summary>
        /// Return how many transactions are opened
        /// </summary>
        public int TransactionsCount => Volatile.Read(ref _readerCount);

        /// <summary>
        /// Enter transaction read lock - should be called just before entering a new transaction
        /// </summary>
        public void EnterTransaction()
        {
            this.EnterTransactionAsync().GetAwaiter().GetResult();
        }

        public ValueTask EnterTransactionAsync(CancellationToken cancellationToken = default)
        {
            var scope = this.GetOrCreateScope();

            if (scope.ExclusiveDepth > 0 || scope.TransactionDepth > 0)
            {
                scope.TransactionDepth++;
                return default;
            }

            return new ValueTask(this.EnterTransactionSlowAsync(scope, cancellationToken));
        }

        /// <summary>
        /// Exit transaction read lock
        /// </summary>
        public void ExitTransaction()
        {
            this.ExitTransactionAsync().GetAwaiter().GetResult();
        }

        public ValueTask ExitTransactionAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return default;
            }

            var scope = _scope.Value;

            if (scope == null || scope.TransactionDepth == 0)
            {
                return default;
            }

            scope.TransactionDepth--;

            if (scope.TransactionDepth > 0 || scope.ExclusiveDepth > 0)
            {
                return default;
            }

            if (!scope.HoldsSharedLock)
            {
                this.TryClearScope(scope);
                return default;
            }

            scope.HoldsSharedLock = false;

            return new ValueTask(this.ExitTransactionSlowAsync(scope));
        }

        /// <summary>
        /// Enter collection write lock mode (only 1 collection per time can have this lock)
        /// </summary>
        public void EnterLock(string collectionName)
        {
            this.EnterLockAsync(collectionName).GetAwaiter().GetResult();
        }

        public ValueTask EnterLockAsync(string collectionName, CancellationToken cancellationToken = default)
        {
            var scope = this.GetOrCreateScope();

            ENSURE(scope.TransactionDepth > 0 || scope.ExclusiveDepth > 0, "Use EnterTransaction() before EnterLock(name)");

            if (scope.CollectionLocks.TryGetValue(collectionName, out var counter))
            {
                scope.CollectionLocks[collectionName] = counter + 1;
                return default;
            }

            var semaphore = _collections.GetOrAdd(collectionName, _ => new SemaphoreSlim(1, 1));

            return new ValueTask(this.EnterCollectionLockAsync(scope, collectionName, semaphore, cancellationToken));
        }

        private async Task EnterCollectionLockAsync(LockScope scope, string collectionName, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            if (!await semaphore.WaitAsync(_pragmas.Timeout, cancellationToken).ConfigureAwait(false))
            {
                throw LiteException.LockTimeout("write", collectionName, _pragmas.Timeout);
            }

            scope.CollectionLocks[collectionName] = 1;
        }

        /// <summary>
        /// Exit collection reserved lock
        /// </summary>
        public void ExitLock(string collectionName)
        {
            var scope = _scope.Value ?? throw LiteException.CollectionLockerNotFound(collectionName);

            if (scope.CollectionLocks.TryGetValue(collectionName, out var counter) == false)
            {
                throw LiteException.CollectionLockerNotFound(collectionName);
            }

            counter--;

            if (counter > 0)
            {
                scope.CollectionLocks[collectionName] = counter;
                return;
            }

            scope.CollectionLocks.Remove(collectionName);

            if (_collections.TryGetValue(collectionName, out var semaphore))
            {
                TryRelease(semaphore);
            }

            this.TryClearScope(scope);
        }

        /// <summary>
        /// Enter all database in exclusive lock. Wait for all transactions to finish. In exclusive mode no one can enter a new transaction (for read/write)
        /// If current context already in exclusive mode, returns false
        /// </summary>
        public bool EnterExclusive()
        {
            return this.EnterExclusiveAsync().GetAwaiter().GetResult();
        }

        public ValueTask<bool> EnterExclusiveAsync(CancellationToken cancellationToken = default)
        {
            var scope = this.GetOrCreateScope();

            if (scope.ExclusiveDepth > 0)
            {
                scope.ExclusiveDepth++;
                return new ValueTask<bool>(false);
            }

            if (scope.TransactionDepth > 0)
            {
                throw new InvalidOperationException("Cannot enter exclusive mode while holding transaction locks.");
            }

            return new ValueTask<bool>(this.EnterExclusiveSlowAsync(scope, cancellationToken));
        }

        private async Task<bool> EnterExclusiveSlowAsync(LockScope scope, CancellationToken cancellationToken)
        {
            if (!await _writerLock.WaitAsync(_pragmas.Timeout, cancellationToken).ConfigureAwait(false))
            {
                throw LiteException.LockTimeout("exclusive", _pragmas.Timeout);
            }

            scope.ExclusiveDepth = 1;
            return true;
        }

        /// <summary>
        /// Try enter in exclusive mode - if not possible, just exit with false (do not wait and no exceptions)
        /// If mustExit returns true, must call ExitExclusive after use
        /// </summary>
        public bool TryEnterExclusive(out bool mustExit)
        {
            var scope = this.GetOrCreateScope();

            if (scope.ExclusiveDepth > 0)
            {
                scope.ExclusiveDepth++;
                mustExit = false;
                return true;
            }

            if (scope.TransactionDepth > 0)
            {
                mustExit = false;
                return false;
            }

            if (_writerLock.Wait(0))
            {
                scope.ExclusiveDepth = 1;
                mustExit = true;
                return true;
            }

            mustExit = false;
            return false;
        }

        /// <summary>
        /// Exit exclusive lock
        /// </summary>
        public void ExitExclusive()
        {
            var scope = _scope.Value ?? throw new SynchronizationLockException("No exclusive lock held by the current context.");

            if (scope.ExclusiveDepth == 0)
            {
                throw new SynchronizationLockException("No exclusive lock held by the current context.");
            }

            scope.ExclusiveDepth--;

            if (scope.ExclusiveDepth > 0)
            {
                return;
            }

            _writerLock.Release();
            this.TryClearScope(scope);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _writerLock.Dispose();
            _readerSemaphore.Dispose();

            foreach (var semaphore in _collections.Values)
            {
                semaphore.Dispose();
            }
        }

        private async Task EnterTransactionSlowAsync(LockScope scope, CancellationToken cancellationToken)
        {
            var timeout = _pragmas.Timeout;

            try
            {
                if (!await _readerSemaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                {
                    throw LiteException.LockTimeout("transaction", timeout);
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var acquiredWriter = false;
            var incrementedReader = false;

            try
            {
                if (_readerCount == 0)
                {
                    try
                    {
                        if (!await _writerLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                        {
                            throw LiteException.LockTimeout("transaction", timeout);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    acquiredWriter = true;
                }

                _readerCount++;
                incrementedReader = true;
            }
            catch
            {
                if (incrementedReader)
                {
                    _readerCount--;

                    if (_readerCount == 0 && acquiredWriter)
                    {
                        TryRelease(_writerLock);
                    }
                }
                else if (acquiredWriter)
                {
                    TryRelease(_writerLock);
                }

                throw;
            }
            finally
            {
                TryRelease(_readerSemaphore);
            }

            scope.TransactionDepth = 1;
            scope.HoldsSharedLock = true;
        }

        private async Task ExitTransactionSlowAsync(LockScope scope)
        {
            try
            {
                await _readerSemaphore.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                _readerCount--;

                if (_readerCount == 0)
                {
                    TryRelease(_writerLock);
                }
            }
            finally
            {
                TryRelease(_readerSemaphore);
            }

            this.TryClearScope(scope);
        }

        private LockScope GetOrCreateScope()
        {
            var scope = _scope.Value;

            if (scope == null)
            {
                scope = new LockScope();
                _scope.Value = scope;
            }

            return scope;
        }

        private void TryClearScope(LockScope scope)
        {
            if (scope.TransactionDepth == 0 && scope.ExclusiveDepth == 0 && scope.CollectionLocks.Count == 0)
            {
                if (ReferenceEquals(_scope.Value, scope))
                {
                    _scope.Value = null;
                }
            }
        }

        private static void TryRelease(SemaphoreSlim semaphore)
        {
            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private sealed class LockScope
        {
            public int TransactionDepth;
            public bool HoldsSharedLock;
            public int ExclusiveDepth;
            public Dictionary<string, int> CollectionLocks { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
