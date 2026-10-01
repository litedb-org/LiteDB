using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>One explicit transaction and all of its bound objects.</summary>
    internal sealed class LiteTransaction : ILiteTransaction
    {
        private readonly object _gate = new object();
        private TransactionResources _resources;
        private TransactionContext _transaction;
        private volatile LiteTransactionState _outcome;
        private LiteDatabaseContext _client;
        private TransactionHandles _handles;
        private readonly HashSet<TransactionReader> _readers = new HashSet<TransactionReader>();
        // Bound objects disposed while another call executes, released when that call returns.
        private List<Action> _deferred;
        private Thread _executing;
        private bool _closing, _disposed;
        private readonly bool _shared;

        internal LiteTransaction(TransactionResources resources, BsonMapper mapper, TransactionHandles handles)
        {
            _resources = resources;
            _handles = handles;
            _shared = resources.SharedMutexName != null;
            _transaction = new TransactionContext(resources.Engine, resources.SharedMutexName);
            _client = new LiteDatabaseContext(new TransactionEngine(this), mapper);
            try
            {
                // A handle is not a nested scope of the caller's legacy transaction.
                if (resources.Engine.CurrentThreadHasLegacyTransaction())
                    throw new InvalidOperationException("Complete the legacy transaction before opening a transaction handle.");
                using var binding = TransactionContext.Enter(_transaction);
                resources.Engine.BeginHandleTransaction();
            }
            catch (Exception error)
            {
                try { resources.Dispose(); }
                catch (Exception cleanup) { error.Data["LiteDB.TransactionOpenCleanup"] = cleanup; }
                throw;
            }
        }

        public LiteTransactionState State => _transaction?.Outcome ?? _outcome;
        internal LiteEngine Storage => _resources?.Engine ?? throw new InvalidOperationException("The transaction has completed.");

        /// <summary>Whether a call of this handle executes on the current thread.</summary>
        internal bool IsExecutingOnCurrentThread
        {
            get { lock (_gate) return ReferenceEquals(_executing, Thread.CurrentThread); }
        }

        /// <summary>Whether a call of this handle executes on any thread.</summary>
        internal bool IsExecuting
        {
            get { lock (_gate) return _executing != null; }
        }

        /// <summary>Database close: refuse calls not yet admitted; an idle handle then stays idle.</summary>
        internal void RefuseNewCalls()
        {
            lock (_gate) _closing = true;
        }

        private void Enter(bool terminalAllowed = false)
        {
            // Before any side effect: the handle stays usable from a non-impersonating thread.
            if (_shared) Client.Shared.TransactionHolderContext.Validate();
            lock (_gate)
            {
                // A sequential call that lands during close sees the close, not an overlap;
                // reentry from the executing call's own callbacks is still reported as such.
                if ((_closing || _disposed) && !ReferenceEquals(_executing, Thread.CurrentThread))
                    throw new ObjectDisposedException(nameof(ILiteTransaction));
                if (_executing != null) throw new InvalidOperationException("Overlapping or reentrant transaction handle use is not supported.");
                if (!terminalAllowed && State != LiteTransactionState.Active)
                    throw new InvalidOperationException("The transaction has completed and its bound objects cannot be reused.");
                _executing = Thread.CurrentThread;
            }
        }

        private void Exit()
        {
            lock (_gate)
            {
                _executing = null;
                // Database close waits for an executing call before rolling back.
                Monitor.PulseAll(_gate);
            }
        }

        internal T Run<T>(Func<T> action, Action validate = null)
        {
            Enter();
            // Child lifetime misuse is an admission refusal, not a statement failure.
            try { validate?.Invoke(); }
            catch { Exit(); throw; }
            return RunCore(action);
        }

        /// <summary>
        /// Detach a bound reader or enumerator and release it, or have the handle's close or
        /// completion release it. Disposal from a callback of the executing call is refused.
        /// </summary>
        internal void DisposeBoundObject(Action detach, Action release)
        {
            lock (_gate)
            {
                if (_closing || _disposed || State != LiteTransactionState.Active) { detach(); return; }
                if (ReferenceEquals(_executing, Thread.CurrentThread))
                    throw new InvalidOperationException("Overlapping transaction disposal is not supported.");
                detach();
                // A foreach ending while another thread's call of this handle executes must not
                // leave its reader registered (which refuses commit): release it after that call.
                if (_executing != null) { (_deferred ?? (_deferred = new List<Action>())).Add(release); return; }
                _executing = Thread.CurrentThread;
            }
            RunCore(() => { release(); return true; });
        }

        private void DisposeDeferred()
        {
            while (_deferred != null)
            {
                List<Action> pending;
                lock (_gate) { pending = _deferred; _deferred = null; }
                foreach (var dispose in pending) dispose();
            }
        }

        private T RunCore<T>(Func<T> action)
        {
            try
            {
                using var binding = TransactionContext.Enter(_transaction);
                try
                {
                    var result = action();
                    DisposeDeferred();
                    return result;
                }
                catch (Exception error)
                {
                    // Refusals before mutation leave the transaction usable. Any other statement
                    // failure, or one whose engine transaction was rolled back, ends the handle.
                    var intact = ReferenceEquals(_transaction.Slot.Transaction, _transaction.Transaction) &&
                        _transaction.Transaction.State == TransactionState.Active;
                    if (!intact || (!(error is TransactionCapabilityException) && !(error is ReadOnlyRefusalException) &&
                        !(_resources.Engine.IsReadOnly && error is NotSupportedException))) Abort(error);
                    throw;
                }
            }
            finally { Exit(); }
        }

        // Only composed client operations may dispatch internally. Public wrappers always use Run.
        internal T Dispatch<T>(Func<T> action, bool authorizeEngine = true)
        {
            lock (_gate)
                if (!ReferenceEquals(_executing, Thread.CurrentThread))
                    throw new InvalidOperationException("A bound engine call requires its transaction operation.");
            using var binding = TransactionContext.Enter(_transaction);
            using var dispatch = TransactionContext.Dispatch(authorizeEngine ? _resources.Engine : null);
            return action();
        }

        private void ReleaseResources(Exception cause = null)
        {
            try { _resources.Dispose(); }
            catch (Exception cleanup)
            {
                if (cause == null) throw;
                cause.Data["LiteDB.TransactionReleaseError"] = cleanup;
            }
            finally
            {
                var handles = _handles;
                _outcome = _transaction.Outcome;
                _transaction = null;
                _resources = null;
                _handles = null;
                _client = null;
                // Completion closed every reader; deferred disposals have nothing left to release.
                lock (_gate) _deferred = null;
                handles.Completed(this);
            }
        }

        private void Abort(Exception cause)
        {
            if (_transaction.Outcome != LiteTransactionState.Active) return;
            _transaction.Outcome = LiteTransactionState.Failed;
            try
            {
                CloseReadersAndRollback(onlyIfActive: true);
            }
            catch (Exception cleanup) { cause.Data["LiteDB.TransactionCleanupError"] = cleanup; }
            finally { ReleaseResources(cause); }
        }

        internal IBsonDataReader Query(string collection, Query query) => Dispatch(() =>
        {
            if ((collection.StartsWith("$") && collection != "$indexes" && collection != "$cols") ||
                query.Into?.StartsWith("$") == true)
                throw new TransactionCapabilityException("External/system collection I/O is not supported inside transaction handles.");
            var reader = new TransactionReader(this, _resources.Engine.Query(collection, query));
            _readers.Add(reader);
            return (IBsonDataReader)reader;
        });

        internal void ReleaseReader(TransactionReader reader)
        {
            reader.Close();
            _readers.Remove(reader);
        }

        private void CloseReaders()
        {
            var errors = new List<Exception>();
            foreach (var reader in _readers.ToArray())
                try { reader.Close(); } catch (Exception error) { errors.Add(error); }
            _readers.Clear();
            if (errors.Count != 0) throw new AggregateException(errors);
        }

        private void CloseReadersAndRollback(bool onlyIfActive = false)
        {
            Exception failure = null;
            try { CloseReaders(); }
            catch (Exception error) { failure = error; }
            try
            {
                if (!onlyIfActive) Dispatch(() => _resources.Engine.Rollback());
                else if (_transaction.Slot.Transaction != null)
                    Dispatch(() => _resources.Engine.RollbackHandleOnDispose());
            }
            catch (Exception error)
            {
                if (failure == null) throw;
                failure.Data["LiteDB.TransactionRollback"] = error;
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Commit()
        {
            Enter();
            try
            {
                using var binding = TransactionContext.Enter(_transaction);
                try { DisposeDeferred(); }
                catch (Exception error) { Abort(error); throw; }
                if (_readers.Count != 0) throw new InvalidOperationException("Close transaction-bound readers before committing.");
                Exception failure = null;
                try
                {
                    if (!Dispatch(() => _resources.Engine.Commit()))
                    {
                        // The engine no longer holds this transaction (a peer stopped or closed the
                        // engine first): this call committed nothing. Never report that as success.
                        _transaction.Outcome = LiteTransactionState.Failed;
                        _resources.Engine.ThrowIfUnavailable();
                        throw new InvalidOperationException("The transaction ended before it could commit; nothing was committed.");
                    }
                }
                catch (Exception error)
                {
                    // A peer engine close surfaces as raw disposal of its services or streams:
                    // report the engine's published failure, as ordinary calls do.
                    failure = (error as ObjectDisposedException != null ? _resources.Engine.UnavailableFailure() : null) ?? error;
                    // A failed commit may already have published: never relabel it a rollback.
                    if (_transaction.Outcome == LiteTransactionState.Active)
                        _transaction.Outcome = LiteTransactionState.Indeterminate;
                    if (failure != error) ExceptionDispatchInfo.Capture(failure).Throw();
                    throw;
                }
                finally { if (_transaction.Outcome != LiteTransactionState.Active) ReleaseResources(failure); }
            }
            finally { Exit(); }
        }

        public void Rollback()
        {
            Enter();
            try { RollbackCore(); }
            finally { Exit(); }
        }

        private void RollbackCore(bool onlyIfActive = false)
        {
            using var binding = TransactionContext.Enter(_transaction);
            Exception failure = null;
            try
            {
                CloseReadersAndRollback(onlyIfActive);
                _transaction.Outcome = LiteTransactionState.RolledBack;
            }
            catch (Exception error) { failure = error; _transaction.Outcome = LiteTransactionState.Failed; throw; }
            finally { ReleaseResources(failure); }
        }

        // Database close already owns rollback. Cleanup is idempotent across that handoff,
        // but normal overlapping/reentrant user operations remain invalid.
        private bool EnterCleanup()
        {
            lock (_gate)
            {
                if (_closing || _disposed || State != LiteTransactionState.Active) return false;
                if (_executing != null) throw new InvalidOperationException("Overlapping transaction disposal is not supported.");
                _executing = Thread.CurrentThread;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
                if (State != LiteTransactionState.Active) { _disposed = true; return; }
            if (!EnterCleanup()) return;
            try { DisposeCore(); }
            finally { Exit(); }
        }

        /// <summary>
        /// Close on behalf of the owning database: refuse new calls, wait for an executing call
        /// of another thread to return, then roll back an active transaction.
        /// </summary>
        internal void CloseForDatabase()
        {
            lock (_gate)
            {
                _closing = true;
                while (_executing != null) Monitor.Wait(_gate);
                if (_disposed) return;
                _executing = Thread.CurrentThread;
            }
            try { DisposeCore(); }
            finally { Exit(); }
        }

        private void DisposeCore()
        {
            try { if (State == LiteTransactionState.Active) RollbackCore(onlyIfActive: true); }
            finally { lock (_gate) _disposed = true; }
        }

        public ILiteCollection<T> GetCollection<T>(string name = null, BsonAutoId autoId = BsonAutoId.ObjectId) =>
            Run(() => (ILiteCollection<T>)new TransactionCollection<T>(this, new LiteCollection<T>(name, autoId, _client)));
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId) =>
            GetCollection<BsonDocument>(name ?? throw new ArgumentNullException(nameof(name)), autoId);
        public IEnumerable<string> GetCollectionNames() => Run(() => Dispatch(() => _resources.Engine.GetTransactionCollectionNames()));
        public bool CollectionExists(string name) => GetCollectionNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        public bool DropCollection(string name) => throw new TransactionCapabilityException("Dropping collections is not supported inside transactions.");
        public bool RenameCollection(string name, string newName) => throw new TransactionCapabilityException("Renaming collections is not supported inside transactions.");
    }

    /// <summary>An operation a transaction handle does not support, refused before mutation.</summary>
    internal sealed class TransactionCapabilityException : NotSupportedException
    {
        internal TransactionCapabilityException(string message) : base(message) { }
    }
}
