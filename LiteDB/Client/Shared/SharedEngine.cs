using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Vector;

namespace LiteDB
{
    /// <summary>
    /// Provides a shared engine implementation that allows multiple processes to access the same database file using mutex-based coordination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SharedEngine"/> wraps a <see cref="LiteEngine"/> and uses a named mutex to coordinate access across processes.
    /// The engine is opened on-demand for operations and closed when not in use, except during active transactions.
    /// </para>
    /// <para>
    /// <b>Note:</b> Shared mode requires platform support for named mutexes and may not be available on all platforms.
    /// </para>
    /// </remarks>
    public class SharedEngine : ILiteEngine
    {
        private readonly EngineSettings _settings;
        private readonly Mutex _mutex;
        private LiteEngine _engine;
        private bool _transactionRunning = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedEngine"/> class with the specified settings.
        /// </summary>
        /// <param name="settings">The engine settings for database configuration.</param>
        /// <exception cref="PlatformNotSupportedException">Thrown when the platform does not support named mutexes.</exception>
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
            try
            {
                // Acquire mutex for every call to open DB.
                _mutex.WaitOne();
            }
            catch (AbandonedMutexException) { }

            // Don't create a new engine while a transaction is running.
            if (!_transactionRunning && _engine == null)
            {
                try
                {
                    _engine = new LiteEngine(_settings);
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

        /// <summary>
        /// Dequeue stack and dispose database on empty stack
        /// </summary>
        private void CloseDatabase()
        {
            // Don't dispose the engine while a transaction is running.
            if (!_transactionRunning && _engine != null)
            {
                // If no transaction pending, dispose the engine.
                _engine.Dispose();
                _engine = null;
            }

            // Release Mutex on every call to close DB.
            _mutex.ReleaseMutex();
        }

        #region Transaction Operations

        /// <inheritdoc/>
        public bool BeginTrans()
        {
            OpenDatabase();

            try
            {
                _transactionRunning = _engine.BeginTrans();

                return _transactionRunning;
            }
            catch
            {
                CloseDatabase();
                throw;
            }
        }

        /// <inheritdoc/>
        public bool Commit()
        {
            if (_engine == null) return false;

            try
            {
                return _engine.Commit();
            }
            finally
            {
                _transactionRunning = false;
                CloseDatabase();
            }
        }

        /// <inheritdoc/>
        public bool Rollback()
        {
            if (_engine == null) return false;

            try
            {
                return _engine.Rollback();
            }
            finally
            {
                _transactionRunning = false;
                CloseDatabase();
            }
        }

        #endregion

        #region Read Operation

        /// <inheritdoc/>
        public IBsonDataReader Query(string collection, Query query)
        {
            bool opened = OpenDatabase();

            var reader = _engine.Query(collection, query);

            return new SharedDataReader(reader, () =>
            {
                if (opened)
                {
                    CloseDatabase();
                }
            });
        }

        /// <inheritdoc/>
        public BsonValue Pragma(string name)
        {
            return QueryDatabase(() => _engine.Pragma(name));
        }

        /// <inheritdoc/>
        public bool Pragma(string name, BsonValue value)
        {
            return QueryDatabase(() => _engine.Pragma(name, value));
        }

        #endregion

        #region Write Operations

        /// <inheritdoc/>
        public int Checkpoint()
        {
            return QueryDatabase(() => _engine.Checkpoint());
        }

        /// <inheritdoc/>
        public long Rebuild(RebuildOptions options)
        {
            return QueryDatabase(() => _engine.Rebuild(options));
        }

        /// <inheritdoc/>
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return QueryDatabase(() => _engine.Insert(collection, docs, autoId));
        }

        /// <inheritdoc/>
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            return QueryDatabase(() => _engine.Update(collection, docs));
        }

        /// <inheritdoc/>
        public int UpdateMany(string collection, BsonExpression extend, BsonExpression predicate)
        {
            return QueryDatabase(() => _engine.UpdateMany(collection, extend, predicate));
        }

        /// <inheritdoc/>
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return QueryDatabase(() => _engine.Upsert(collection, docs, autoId));
        }

        /// <inheritdoc/>
        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            return QueryDatabase(() => _engine.Delete(collection, ids));
        }

        /// <inheritdoc/>
        public int DeleteMany(string collection, BsonExpression predicate)
        {
            return QueryDatabase(() => _engine.DeleteMany(collection, predicate));
        }

        /// <inheritdoc/>
        public bool DropCollection(string name)
        {
            return QueryDatabase(() => _engine.DropCollection(name));
        }

        /// <inheritdoc/>
        public bool RenameCollection(string name, string newName)
        {
            return QueryDatabase(() => _engine.RenameCollection(name, newName));
        }

        /// <inheritdoc/>
        public bool DropIndex(string collection, string name)
        {
            return QueryDatabase(() => _engine.DropIndex(collection, name));
        }

        /// <inheritdoc/>
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        {
            return QueryDatabase(() => _engine.EnsureIndex(collection, name, expression, unique));
        }

        /// <inheritdoc/>
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        {
            return QueryDatabase(() => _engine.EnsureVectorIndex(collection, name, expression, options));
        }

        #endregion

        /// <summary>
        /// Releases all resources used by the <see cref="SharedEngine"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer for the <see cref="SharedEngine"/> class.
        /// </summary>
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
                if (opened)
                {
                    CloseDatabase();
                }
            }
        }
    }
}