using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB
{
    public partial class SharedEngine : ILiteEngine
    {
        // An operation's engine closes without checkpoint until the WAL reaches this many
        // pages: replaying up to 400 KiB at the next open costs less than the checkpoint's syncs.
        internal const int CLOSE_CHECKPOINT_PAGES = 50;

        private readonly EngineSettings _settings;
        private readonly Mutex _mutex;
        private readonly SharedMutexOwner _owner;
        // Guards the engine's user count, which a reader disposed on another thread also updates.
        private readonly object _useLock = new object();
        private readonly SharedReaderRegistry _readers;
        private LiteEngine _engine;
        private WalRecoveryReport _recoveryReport;
        private volatile bool _transactionRunning = false;
        private int _transactionThreadId;
        private int _databaseUsers;
        private SharedMutexPin _transactionUse;
        private int _disposed;
#if DEBUG || TESTING
        internal Func<LiteEngine> SimulateOpenEngine { get; set; }

        internal int EngineOpens { get; private set; }

        internal SharedMutexOwner MutexOwner => _owner;
#endif

        public SharedEngine(EngineSettings settings)
        {
            _settings = settings.Clone();
            // Reopens must use the same path as the mutex and snapshot registry,
            // even if the process changes its working directory between calls.
            if (_settings.Filename != ":memory:" && _settings.Filename != ":temp:")
                _settings.Filename = Path.GetFullPath(_settings.Filename);
            _settings.SharedDurability = new SharedDurabilityState();
            _readers = new SharedReaderRegistry(_settings.Filename, _settings.SharedReaderFiles);
            _settings.SharedReaderVersions = _readers.LiveVersions;
            // Each operation opens and closes an engine. Share one back-off so a
            // long-lived reader cannot make every close pay for partial checkpoint.
            _settings.CheckpointBackoff = new CheckpointBackoff();
            _settings.CloseCheckpointPages = CLOSE_CHECKPOINT_PAGES;

            var name = SharedMutexNameFactory.Create(_settings.Filename, _settings.SharedMutexNameStrategy);

            try
            {
                _mutex = SharedMutexFactory.Create(name);
            }
            catch (NotSupportedException ex)
            {
                if (ex is PlatformNotSupportedException)
                {
                    throw;
                }

                throw new PlatformNotSupportedException("Shared mode is not supported in platforms that do not implement named mutex.", ex);
            }
            _owner = new SharedMutexOwner(_mutex, this.OnOwnerExited);
        }

        /// <summary>
        /// Open database in safe mode. Returns the pin the operation runs under, or
        /// null when the operation owns a recursion of the named mutex instead.
        /// </summary>
        private SharedMutexPin OpenDatabase()
        {
            var pin = _pin;
            if (pin != null)
            {
                if (pin.TryEnter()) return pin;
                // Another thread needs the mutex: end the pin at its next idle moment.
                if (!ReferenceEquals(pin.Owner, Thread.CurrentThread)) pin.RequestRelease(force: false);
            }

            // Acquire mutex for every call to open DB.
            var recoveredAbandonedOwner = _owner.Enter();

            try
            {
                RejectAbandonedTransaction();
            }
            catch { _owner.Exit(); throw; }

            // Don't create a new engine while a transaction is running.
            if (!_transactionRunning && _engine == null)
            {
                try
                {
                    this.OpenEngine(recoveredAbandonedOwner);
                }
                catch
                {
                    _owner.Exit();
                    throw;
                }
            }
            lock (_useLock) _databaseUsers++;
            return null;
        }

        private void OpenEngine(bool recoveredAbandonedOwner)
        {
            _engine = this.CreateEngine(recoveredAbandonedOwner);
#if DEBUG || TESTING
            this.EngineOpens++;
#endif
            _recoveryReport = _engine.RecoveryReport ?? _recoveryReport;
            _engine.RecoveryReport = _recoveryReport;
        }

        private LiteEngine CreateEngine(bool recoveredAbandonedOwner)
        {
            const int retries = 100;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
#if DEBUG || TESTING
                    if (SimulateOpenEngine != null) return SimulateOpenEngine();
#endif
                    var settings = _settings;
                    if (settings.AutoRebuild && _readers.OldestVersion().HasValue)
                    {
                        settings = settings.Clone();
                        settings.AutoRebuild = false;
                    }
                    return new LiteEngine(settings);
                }
                catch (IOException ex) when (recoveredAbandonedOwner && IsWindowsLockViolation(ex) && attempt < retries)
                {
                    // On Windows an abandoned mutex can become available just before
                    // the dead process' file handles finish closing. Keep ownership
                    // while the transient sharing violation clears.
                    Thread.Sleep(20);
                }
            }
        }

        private static bool IsWindowsLockViolation(IOException exception)
        {
            const int ERROR_SHARING_VIOLATION = 32;
            const int ERROR_LOCK_VIOLATION = 33;
            var errorCode = exception.HResult & 0xFFFF;
            return errorCode == ERROR_SHARING_VIOLATION || errorCode == ERROR_LOCK_VIOLATION;
        }

        /// <summary>
        /// Dequeue stack and dispose database on empty stack. A pinned use ends an
        /// operation, or with <paramref name="hold"/> a reader or transaction.
        /// </summary>
        private void CloseDatabase(SharedMutexPin use = null, bool hold = false, int generation = -1)
        {
            if (use != null)
            {
                // The pin keeps the engine; its holder closes it.
                use.Exit(hold);
                return;
            }

            try
            {
                lock (_useLock)
                {
                    // A reader of an ownership that already ended (Dispose, exited
                    // owner) was counted by that ownership, which reset the count.
                    if (generation >= 0 && generation != _owner.Generation) return;
                    if (_databaseUsers > 0 && --_databaseUsers == 0 && !_transactionRunning && _engine != null)
                    {
                        var engine = _engine;
                        _engine = null;
                        engine.Close();
                    }
                }
            }
            finally
            {
                if (!_transactionRunning) _transactionThreadId = 0;
                // Every OpenDatabase call acquires a recursion, even when it borrows.
                // Any thread may end it, for example when disposing a reader.
                _owner.Exit(generation);
            }
        }

        /// <summary>
        /// Runs on the mutex holder thread when the owner thread exited while owning
        /// the mutex. The mutex was never released meanwhile, but the owner's open
        /// reader or transaction can no longer complete: drop the engine without
        /// writing. An exited transaction owner is reported to the next caller.
        /// </summary>
        private void OnOwnerExited()
        {
            lock (_useLock)
            {
                _databaseUsers = 0;
                var engine = _engine;
                _engine = null;
                engine?.Close(checkpoint: false);
            }
        }

        #region Transaction Operations

        public bool BeginTrans()
        {
            var use = OpenDatabase();

            try
            {
                var started = _engine.BeginTrans();
                if (started)
                {
                    _transactionThreadId = Environment.CurrentManagedThreadId;
                    _transactionRunning = true;
                    // A pinned transaction keeps the pin until it completes.
                    _transactionUse = use;
                    use?.ToHold();
                }
                // A false join belongs to the surrounding explicit or automatic
                // transaction; its caller owes no completion or mutex recursion.
                else CloseDatabase(use);
                return started;
            }
            catch
            {
                CloseDatabase(use);
                throw;
            }
        }

        public bool Commit() => CompleteTransaction(commit: true);

        public bool Rollback() => CompleteTransaction(commit: false);

        private bool CompleteTransaction(bool commit)
        {
            // Hold one extra mutex recursion (or pinned operation) throughout
            // completion. A foreign thread must not reach cleanup, even while
            // BeginTrans is publishing.
            var pin = _pin;
            var pinned = pin != null && pin.TryEnter();
            if (!pinned && !_owner.TryEnter(out _))
            {
                // Rolling back nothing is safe and must not replace the error a catch block is handling.
                if (!_transactionRunning || !commit) return false;
                throw ForeignTransactionCompletion();
            }

            try
            {
                RejectAbandonedTransaction();
                if (!_transactionRunning || _engine == null) return false;
                try { return commit ? _engine.Commit() : _engine.Rollback(); }
                finally
                {
                    var use = _transactionUse;
                    _transactionUse = null;
                    _transactionRunning = false;
                    CloseDatabase(use, hold: true);
                }
            }
            finally
            {
                if (pinned) pin.Exit(hold: false);
                else _owner.Exit();
            }
        }

        private void RejectAbandonedTransaction()
        {
            // Called only while owning the named mutex. A live explicit owner
            // retains a recursion, so acquisition on another thread proves that
            // ownership was abandoned, even if another instance consumed the signal.
            if (!_transactionRunning || _transactionThreadId == Environment.CurrentManagedThreadId) return;
            _transactionRunning = false;
            _transactionThreadId = 0;
            _databaseUsers = 0;
            var orphan = _engine;
            _engine = null;
            // The mutex was free since the owner exited, so another process may have
            // committed or checkpointed. This engine's WAL index and cache can be stale:
            // release it without the close checkpoint; the next open recovers the WAL.
            orphan?.Close(checkpoint: false);
            throw new LiteException(0, "The explicit transaction owner thread exited. Its uncommitted work was discarded; begin a new transaction on one thread.");
        }

        private static LiteException ForeignTransactionCompletion() =>
            new LiteException(0, "Complete the explicit transaction on the same thread that called BeginTrans; do not await inside it.");

        #endregion

        #region Read Operation

        public BsonValue Pragma(string name)
        {
            return QueryDatabase(() => _engine.Pragma(name));
        }

        public bool Pragma(string name, BsonValue value)
        {
            return WriteDatabase(() => _engine.Pragma(name, value));
        }

        #endregion

        #region Write Operations

        public int Checkpoint()
        {
            return WriteDatabase(() => _engine.Checkpoint());
        }

        public long Rebuild(RebuildOptions options)
        {
            return WriteDatabase(() =>
            {
                if (_readers.OldestVersion().HasValue)
                    throw new LiteException(0, "Close shared readers before rebuilding the database.");
                return _engine.Rebuild(options);
            });
        }

        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return WriteDatabase(() => _engine.Insert(collection, docs, autoId));
        }

        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            return WriteDatabase(() => _engine.Update(collection, docs));
        }

        public int UpdateMany(string collection, BsonExpression extend, BsonExpression predicate)
        {
            return WriteDatabase(() => _engine.UpdateMany(collection, extend, predicate));
        }

        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return WriteDatabase(() => _engine.Upsert(collection, docs, autoId));
        }

        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            return WriteDatabase(() => _engine.Delete(collection, ids));
        }

        public int DeleteMany(string collection, BsonExpression predicate)
        {
            return WriteDatabase(() => _engine.DeleteMany(collection, predicate));
        }

        public bool DropCollection(string name)
        {
            return WriteDatabase(() => _engine.DropCollection(name));
        }

        public bool RenameCollection(string name, string newName)
        {
            return WriteDatabase(() => _engine.RenameCollection(name, newName));
        }

        public bool DropIndex(string collection, string name)
        {
            return WriteDatabase(() => _engine.DropIndex(collection, name));
        }

        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        {
            return WriteDatabase(() => _engine.EnsureIndex(collection, name, expression, unique));
        }

        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        {
            return WriteDatabase(() => _engine.EnsureVectorIndex(collection, name, expression, options));
        }

        #endregion

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
            if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Any thread can end a pin; its holder closes the engine and releases.
            var pin = _pin;
            if (pin != null)
            {
                pin.RequestRelease(force: true);
                if (!pin.CanWaitFrom(Thread.CurrentThread)) return;
                pin.WaitReleased();
            }

            var closed = false;
            lock (_useLock)
            {
                if (_engine != null)
                {
                    _engine.Close(final: true);
                    _engine = null;
                    closed = true;
                }
                _databaseUsers = 0;
            }
            // Open readers and transactions of any thread end with the connection.
            _owner.ReleaseAll();
            // Operations left a WAL below the close threshold: checkpoint it now, so
            // the data file alone is the database again once every connection closed.
            if (!closed) this.CheckpointOnDispose();
            // A disposed connection holds no mutex, even for the moment its holder
            // needs to release it; another connection's final close may try it next.
            _owner.WaitForRelease();
        }

        private T QueryDatabase<T>(Func<T> Query)
        {
            var use = OpenDatabase();
            try
            {
                return Query();
            }
            finally
            {
                CloseDatabase(use);
            }
        }
    }
}
