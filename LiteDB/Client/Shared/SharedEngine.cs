using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

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
                _mutex = CreateMutex(name);
            }
            catch (NotSupportedException ex)
            {
                throw new PlatformNotSupportedException("Shared mode is not supported in platforms that do not implement named mutex.", ex);
            }
        }

        private static Mutex CreateMutex(string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new Mutex(false, "Global\\" + name + ".Mutex");
            }

            var allowEveryoneRule = new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.FullControl, AccessControlType.Allow);

            var securitySettings = new MutexSecurity();
            securitySettings.AddAccessRule(allowEveryoneRule);

            return MutexAcl.Create(false, "Global\\" + name + ".Mutex", out _, securitySettings);
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
            return QueryDatabase(() => _engine.Rebuild(options));
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
                if (opened)
                {
                    CloseDatabase();
                }
            }
        }
    }
}