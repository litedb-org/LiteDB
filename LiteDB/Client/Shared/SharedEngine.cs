using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace LiteDB
{
    public class SharedEngine : ILiteEngine
    {
        private readonly EngineSettings _settings;
        private readonly Mutex _mutex;
        private LiteEngine _engine;
        private bool _transactionRunning = false;

        public SharedEngine(EngineSettings settings)
        {
            _settings = settings;

            var name = Path.GetFullPath(settings.Filename).ToLower().Sha1();

            try
            {
#if NETFRAMEWORK
                var allowEveryoneRule = new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                           MutexRights.FullControl, AccessControlType.Allow);

                var securitySettings = new MutexSecurity();
                securitySettings.AddAccessRule(allowEveryoneRule);

                _mutex = new Mutex(false, "Global\\" + name + ".Mutex", out _, securitySettings);
#else
                _mutex = new Mutex(false, "Global\\" + name + ".Mutex");
#endif
            }
            catch (NotSupportedException ex)
            {
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

        public async Task<bool> BeginTransAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OpenDatabase();

            try
            {
                _transactionRunning = await _engine.BeginTransAsync(cancellationToken).ConfigureAwait(false);

                return _transactionRunning;
            }
            catch
            {
                CloseDatabase();
                throw;
            }
        }

        public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_engine == null) return false;

            try
            {
                return await _engine.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transactionRunning = false;
                CloseDatabase();
            }
        }

        public async Task<bool> RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_engine == null) return false;

            try
            {
                return await _engine.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transactionRunning = false;
                CloseDatabase();
            }
        }

        #endregion

        #region Read Operation

        public async Task<IBsonDataReader> QueryAsync(string collection, Query query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var opened = OpenDatabase();

            try
            {
                var reader = await _engine.QueryAsync(collection, query, cancellationToken).ConfigureAwait(false);

                return new SharedDataReader(reader, () =>
                {
                    if (opened)
                    {
                        CloseDatabase();
                    }
                });
            }
            catch
            {
                if (opened)
                {
                    CloseDatabase();
                }

                throw;
            }
        }

        public Task<BsonValue> PragmaAsync(string name, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.PragmaAsync(name, cancellationToken), cancellationToken);
        }

        public Task<bool> PragmaAsync(string name, BsonValue value, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.PragmaAsync(name, value, cancellationToken), cancellationToken);
        }

        #endregion

        #region Write Operations

        public Task<int> CheckpointAsync(CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.CheckpointAsync(cancellationToken), cancellationToken);
        }

        public Task<long> RebuildAsync(RebuildOptions options, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.RebuildAsync(options, cancellationToken), cancellationToken);
        }

        public Task<int> InsertAsync(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.InsertAsync(collection, docs, autoId, cancellationToken), cancellationToken);
        }

        public Task<int> UpdateAsync(string collection, IEnumerable<BsonDocument> docs, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.UpdateAsync(collection, docs, cancellationToken), cancellationToken);
        }

        public Task<int> UpdateManyAsync(string collection, BsonExpression extend, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.UpdateManyAsync(collection, extend, predicate, cancellationToken), cancellationToken);
        }

        public Task<int> UpsertAsync(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.UpsertAsync(collection, docs, autoId, cancellationToken), cancellationToken);
        }

        public Task<int> DeleteAsync(string collection, IEnumerable<BsonValue> ids, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.DeleteAsync(collection, ids, cancellationToken), cancellationToken);
        }

        public Task<int> DeleteManyAsync(string collection, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.DeleteManyAsync(collection, predicate, cancellationToken), cancellationToken);
        }

        public Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.DropCollectionAsync(name, cancellationToken), cancellationToken);
        }

        public Task<bool> RenameCollectionAsync(string name, string newName, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.RenameCollectionAsync(name, newName, cancellationToken), cancellationToken);
        }

        public Task<bool> DropIndexAsync(string collection, string name, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.DropIndexAsync(collection, name, cancellationToken), cancellationToken);
        }

        public Task<bool> EnsureIndexAsync(string collection, string name, BsonExpression expression, bool unique, CancellationToken cancellationToken = default)
        {
            return this.QueryDatabaseAsync(() => _engine.EnsureIndexAsync(collection, name, expression, unique, cancellationToken), cancellationToken);
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

        public async ValueTask DisposeAsync()
        {
            if (_engine != null)
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
                _engine = null;
                _mutex.ReleaseMutex();
            }

            GC.SuppressFinalize(this);
        }

        private async Task<T> QueryDatabaseAsync<T>(Func<Task<T>> query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var opened = OpenDatabase();
            try
            {
                return await query().ConfigureAwait(false);
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
