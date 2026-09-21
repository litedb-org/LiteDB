using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Vector;

namespace LiteDB
{
    public class SharedEngine : ILiteEngine
    {
        private readonly EngineSettings _settings;
        private readonly Mutex _mutex;
        private LiteEngine _engine;
        private volatile bool _transactionRunning = false;
        private int _transactionThreadId;
        private volatile Exception _incompleteRebuild;
#if DEBUG || TESTING
        internal Func<LiteEngine> SimulateOpenEngine { get; set; }
#endif

        public SharedEngine(EngineSettings settings)
        {
            _settings = settings;

            var name = SharedMutexNameFactory.Create(settings.Filename, settings.SharedMutexNameStrategy);

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
        }

        /// <summary>
        /// Open database in safe mode
        /// </summary>
        /// <returns>true if successfully opened; false if already open</returns>
        private bool OpenDatabase()
        {
            // An incomplete rebuild rollback leaves the live path missing or holding the
            // original data without its WAL. Opening it would create an empty database or
            // serve one that lacks acknowledged commits, so this handle stays closed.
            var incompleteRebuild = _incompleteRebuild;
            if (incompleteRebuild != null) throw LiteException.RebuildRollbackIncomplete(_settings.Filename, incompleteRebuild);

            var recoveredAbandonedOwner = false;
            try
            {
                // Acquire mutex for every call to open DB.
                _mutex.WaitOne();
            }
            catch (AbandonedMutexException) { recoveredAbandonedOwner = true; }

            try { RejectAbandonedTransaction(); }
            catch { _mutex.ReleaseMutex(); throw; }

            // Don't create a new engine while a transaction is running.
            if (!_transactionRunning && _engine == null)
            {
                try
                {
                    _engine = OpenEngine(recoveredAbandonedOwner);
                    return true;
                }
                catch
                {
                    _mutex.ReleaseMutex();
                    throw;
                }
            }
            else
            {
                return false;
            }
        }

        private LiteEngine OpenEngine(bool recoveredAbandonedOwner)
        {
            const int retries = 100;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
#if DEBUG || TESTING
                    if (SimulateOpenEngine != null) return SimulateOpenEngine();
#endif
                    return new LiteEngine(_settings);
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
        /// Dequeue stack and dispose database on empty stack
        /// </summary>
        private void CloseDatabase(bool ownsEngine = true)
        {
            try
            {
                // Nested operations borrow an engine owned by a transaction or reader.
                if (ownsEngine && !_transactionRunning && _engine != null)
                {
                    var engine = _engine;
                    _engine = null;
                    engine.Dispose();
                }
            }
            finally
            {
                if (ownsEngine && !_transactionRunning) _transactionThreadId = 0;
                // Every OpenDatabase call acquires a recursion, even when it borrows.
                _mutex.ReleaseMutex();
            }
        }

        #region Transaction Operations

        public bool BeginTrans()
        {
            var opened = OpenDatabase();

            try
            {
                var started = _engine.BeginTrans();
                if (started)
                {
                    _transactionThreadId = Environment.CurrentManagedThreadId;
                    _transactionRunning = true;
                }
                // A false join belongs to the surrounding explicit or automatic
                // transaction; its caller owes no completion or mutex recursion.
                else CloseDatabase(opened);
                return started;
            }
            catch
            {
                CloseDatabase(opened);
                throw;
            }
        }

        public bool Commit() => CompleteTransaction(commit: true);

        public bool Rollback() => CompleteTransaction(commit: false);

        private bool CompleteTransaction(bool commit)
        {
            // Hold one extra mutex recursion throughout completion. A foreign
            // thread must not reach cleanup, even while BeginTrans is publishing.
            try
            {
                if (!_mutex.WaitOne(0))
                {
                    // Rolling back nothing is safe and must not replace the error a catch block is handling.
                    if (!_transactionRunning || !commit) return false;
                    throw ForeignTransactionCompletion();
                }
            }
            catch (AbandonedMutexException) { }

            try
            {
                RejectAbandonedTransaction();
                if (!_transactionRunning || _engine == null) return false;
                try { return commit ? _engine.Commit() : _engine.Rollback(); }
                finally
                {
                    _transactionRunning = false;
                    CloseDatabase();
                }
            }
            finally { _mutex.ReleaseMutex(); }
        }

        private void RejectAbandonedTransaction()
        {
            // Called only while owning the named mutex. A live explicit owner
            // retains a recursion, so acquisition on another thread proves that
            // ownership was abandoned, even if another instance consumed the signal.
            if (!_transactionRunning || _transactionThreadId == Environment.CurrentManagedThreadId) return;
            _transactionRunning = false;
            _transactionThreadId = 0;
            var orphan = _engine;
            _engine = null;
            orphan?.Dispose();
            throw new LiteException(0, "The explicit transaction owner thread exited. Its uncommitted work was discarded; begin a new transaction on one thread.");
        }

        private static LiteException ForeignTransactionCompletion() =>
            new LiteException(0, "Complete the explicit transaction on the same thread that called BeginTrans; do not await inside it.");

        #endregion

        #region Read Operation

        public IBsonDataReader Query(string collection, Query query)
        {
            bool opened = OpenDatabase();
            try
            {
                var reader = _engine.Query(collection, query);
                return new SharedDataReader(reader, () => CloseDatabase(opened));
            }
            catch
            {
                CloseDatabase(opened);
                throw;
            }
        }

        public BsonValue Pragma(string name)
        {
            return QueryDatabase(() => _engine.Pragma(name));
        }

        public bool Pragma(string name, BsonValue value)
        {
            return QueryDatabase(() => _engine.Pragma(name, value));
        }

        #endregion

        #region Write Operations

        public int Checkpoint()
        {
            return QueryDatabase(() => _engine.Checkpoint());
        }

        public long Rebuild(RebuildOptions options)
        {
            try
            {
                return QueryDatabase(() => _engine.Rebuild(options));
            }
            catch (Exception ex) when (RebuildService.IsIncomplete(ex))
            {
                _incompleteRebuild = ex;
                throw;
            }
        }

        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return QueryDatabase(() => _engine.Insert(collection, docs, autoId));
        }

        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            return QueryDatabase(() => _engine.Update(collection, docs));
        }

        public int UpdateMany(string collection, BsonExpression extend, BsonExpression predicate)
        {
            return QueryDatabase(() => _engine.UpdateMany(collection, extend, predicate));
        }

        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return QueryDatabase(() => _engine.Upsert(collection, docs, autoId));
        }

        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            return QueryDatabase(() => _engine.Delete(collection, ids));
        }

        public int DeleteMany(string collection, BsonExpression predicate)
        {
            return QueryDatabase(() => _engine.DeleteMany(collection, predicate));
        }

        public bool DropCollection(string name)
        {
            return QueryDatabase(() => _engine.DropCollection(name));
        }

        public bool RenameCollection(string name, string newName)
        {
            return QueryDatabase(() => _engine.RenameCollection(name, newName));
        }

        public bool DropIndex(string collection, string name)
        {
            return QueryDatabase(() => _engine.DropIndex(collection, name));
        }

        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        {
            return QueryDatabase(() => _engine.EnsureIndex(collection, name, expression, unique));
        }

        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        {
            return QueryDatabase(() => _engine.EnsureVectorIndex(collection, name, expression, options));
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
            if (disposing)
            {
                if (_engine != null)
                {
                    _engine.Dispose();
                    _engine = null;
                    _mutex.ReleaseMutex();
                }
            }
        }

        private T QueryDatabase<T>(Func<T> Query)
        {
            bool opened = OpenDatabase();
            try
            {
                return Query();
            }
            finally
            {
                CloseDatabase(opened);
            }
        }
    }
}
