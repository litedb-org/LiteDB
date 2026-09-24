#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Client.Coordinated;
using LiteDB.Vector;

namespace LiteDB.Engine
{
    /// <summary>
    /// EXPERIMENTAL multi-process mode. The first process to open a database becomes
    /// its coordinator and keeps the only writable engine open; the others send writes
    /// to it over a named pipe and read the files directly from snapshots registered
    /// with it. When the coordinator exits, the next call elects a new one, which
    /// recovers the WAL. Every process opening the file must use this engine. See
    /// docs/experimental-coordinator.md for the design, limits and failure semantics.
    /// </summary>
    [Experimental(DiagnosticId)]
    public sealed class CoordinatedEngine : ILiteEngine
    {
        public const string DiagnosticId = "LITEDB_EXPERIMENTAL_COORDINATOR";

        private static readonly TimeSpan ElectionTimeout = TimeSpan.FromSeconds(20);

        private readonly EngineSettings _settings;
        private readonly string _mutexName;
        private readonly object _roleLock = new object();
        private readonly ThreadLocal<int> _transaction = new ThreadLocal<int>();
        private CoordinatorHost _host;
        private CoordinatorClient _client;
        private int _generation;
        private int _disposed;

        public CoordinatedEngine(string filename)
            : this(new EngineSettings { Filename = filename })
        {
        }

        public CoordinatedEngine(EngineSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(settings.Filename) || settings.Filename == ":memory:" || settings.Filename == ":temp:" ||
                settings.DataStream != null || settings.LogStream != null)
                throw new ArgumentException("The coordinator requires a database file.", nameof(settings));
            if (settings.ReadOnly) throw new NotSupportedException("A coordinated connection cannot be read-only.");
            _settings = settings.Clone();
            _settings.Filename = Path.GetFullPath(settings.Filename);
            _mutexName = CoordinatorProtocol.MutexName(_settings.Filename);
            lock (_roleLock) this.Elect();
        }

        /// <summary>True while this process is the coordinator.</summary>
        public bool IsCoordinator
        {
            get { lock (_roleLock) return _host != null; }
        }

        internal int Elections => _generation;
        internal long DirectReads => _client?.DirectReads ?? 0;
        internal long IpcReads => _client?.IpcReads ?? 0;
        internal long SnapshotOpens => _client?.SnapshotOpens ?? 0;
        internal bool HasCachedSnapshot => _client?.HasCachedSnapshot ?? false;

        /// <summary>Test hook: stop coordinating as a killed process would (no checkpoint, no cleanup).</summary>
        internal void CrashCoordinator()
        {
            lock (_roleLock)
            {
                _host?.Crash();
                _host = null;
            }
        }

        #region Role

        private void Elect()
        {
            var deadline = DateTime.UtcNow + ElectionTimeout;
            while (true)
            {
                var mutex = CoordinatorMutex.TryAcquire(_mutexName);
                if (mutex != null)
                {
                    try { _host = new CoordinatorHost(_settings, mutex); }
                    catch
                    {
                        mutex.Dispose();
                        throw;
                    }
                    _generation++;
                    return;
                }
                try
                {
                    _client = CoordinatorClient.Connect(_settings, TimeSpan.FromMilliseconds(500));
                    _generation++;
                    return;
                }
                catch (IOException) { }
                catch (TimeoutException) { }
                if (DateTime.UtcNow > deadline)
                    throw new LiteException(0, "Could not reach or become the coordinator of '" + _settings.Filename + "'.");
                Thread.Sleep(20);
            }
        }

        private (CoordinatorHost Host, CoordinatorClient Client, int Generation) Role()
        {
            lock (_roleLock)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(CoordinatedEngine));
                if (_host == null && _client == null) this.Elect();
                return (_host, _client, _generation);
            }
        }

        private void Lost(CoordinatorClient client)
        {
            lock (_roleLock)
            {
                if (_client != client) return;
                _client.Dispose();
                _client = null;
            }
        }

        private T Execute<T>(Func<LiteEngine, T> local, Func<CoordinatorClient, T> remote, bool retrySafe)
        {
            while (true)
            {
                var (host, client, generation) = this.Role();
                if (_transaction.Value != 0 && _transaction.Value != generation)
                {
                    _transaction.Value = 0;
                    throw new LiteException(0, "The coordinator exited; this thread's explicit transaction was aborted.");
                }
                if (host != null) return host.Run(local);
                try { return remote(client); }
                catch (CoordinatorLostException)
                {
                    this.Lost(client);
                    if (_transaction.Value != 0)
                    {
                        _transaction.Value = 0;
                        throw new LiteException(0, "The coordinator exited; this thread's explicit transaction was aborted.");
                    }
                    if (retrySafe) continue;
                    throw new LiteException(0, "The coordinator exited during this operation; its outcome is unknown.");
                }
            }
        }

        private int Stream(string op, string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId,
            Func<LiteEngine, int> local)
        {
            var header = new BsonDocument { ["op"] = op, ["c"] = collection, ["a"] = (int)autoId };
            return this.Execute(local, c => c.Stream(header, docs), retrySafe: false);
        }

        #endregion

        #region Transactions

        public bool BeginTrans()
        {
            var begun = this.Execute(e => e.BeginTrans(), c => c.Call(new BsonDocument { ["op"] = "begin" }).AsBoolean, false);
            if (begun) _transaction.Value = this.Role().Generation;
            return begun;
        }

        public bool Commit() => this.EndTransaction("commit", e => e.Commit());

        public bool Rollback() => this.EndTransaction("rollback", e => e.Rollback());

        private bool EndTransaction(string op, Func<LiteEngine, bool> local)
        {
            try { return this.Execute(local, c => c.Call(new BsonDocument { ["op"] = op }).AsBoolean, false); }
            finally { _transaction.Value = 0; }
        }

        #endregion

        #region Reads

        public IBsonDataReader Query(string collection, Query query)
        {
            var writes = query.ForUpdate || query.Into != null;
            return this.Execute(e => writes
                    // A write query performs its writes while reading, so it completes under the gate.
                    ? new Client.Shared.BufferedDataReader(
                        CoordinatorProtocol.Values(CoordinatorProtocol.Rows(e.Query(collection, query))), collection)
                    : e.Query(collection, query),
                client =>
                {
                    // Inside a transaction only the coordinator sees this thread's uncommitted writes.
                    if (writes || _transaction.Value != 0) return client.QueryOverIpc(collection, query);
                    return client.TryQueryDirect(collection, query) ?? client.QueryOverIpc(collection, query);
                }, retrySafe: !writes);
        }

        public BsonValue Pragma(string name) =>
            this.Execute(e => e.Pragma(name), c => c.Call(new BsonDocument { ["op"] = "pragma", ["n"] = name }), true);

        #endregion

        #region Writes

        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) =>
            this.Stream("insert", collection, docs, autoId, e => e.Insert(collection, docs, autoId));

        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) =>
            this.Stream("upsert", collection, docs, autoId, e => e.Upsert(collection, docs, autoId));

        public int Update(string collection, IEnumerable<BsonDocument> docs) =>
            this.Stream("update", collection, docs, BsonAutoId.ObjectId, e => e.Update(collection, docs));

        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) =>
            this.Execute(e => e.UpdateMany(collection, transform, predicate), c => c.Call(new BsonDocument
            {
                ["op"] = "updateMany", ["c"] = collection,
                ["t"] = CoordinatorProtocol.Expression(transform), ["p"] = CoordinatorProtocol.Expression(predicate)
            }).AsInt32, false);

        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            var list = ids.ToList();
            return this.Execute(e => e.Delete(collection, list),
                c => c.Call(new BsonDocument { ["op"] = "delete", ["c"] = collection, ["ids"] = new BsonArray(list) }).AsInt32, false);
        }

        public int DeleteMany(string collection, BsonExpression predicate) =>
            this.Execute(e => e.DeleteMany(collection, predicate), c => c.Call(new BsonDocument
            {
                ["op"] = "deleteMany", ["c"] = collection, ["p"] = CoordinatorProtocol.Expression(predicate)
            }).AsInt32, false);

        public bool DropCollection(string name) =>
            this.Execute(e => e.DropCollection(name), c => c.Call(new BsonDocument { ["op"] = "dropCollection", ["n"] = name }).AsBoolean, false);

        public bool RenameCollection(string name, string newName) =>
            this.Execute(e => e.RenameCollection(name, newName),
                c => c.Call(new BsonDocument { ["op"] = "rename", ["n"] = name, ["nn"] = newName }).AsBoolean, false);

        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) =>
            this.Execute(e => e.EnsureIndex(collection, name, expression, unique), c => c.Call(new BsonDocument
            {
                ["op"] = "ensureIndex", ["c"] = collection, ["n"] = name,
                ["e"] = CoordinatorProtocol.Expression(expression), ["u"] = unique
            }).AsBoolean, false);

        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options) =>
            this.Execute(e => e.EnsureVectorIndex(collection, name, expression, options),
                _ => throw new NotSupportedException("Vector index creation must run in the coordinator process."), false);

        public bool DropIndex(string collection, string name) =>
            this.Execute(e => e.DropIndex(collection, name),
                c => c.Call(new BsonDocument { ["op"] = "dropIndex", ["c"] = collection, ["n"] = name }).AsBoolean, false);

        public bool Pragma(string name, BsonValue value) =>
            this.Execute(e => e.Pragma(name, value),
                c => c.Call(new BsonDocument { ["op"] = "setPragma", ["n"] = name, ["v"] = value }).AsBoolean, false);

        public int Checkpoint() =>
            this.Execute(e => e.Checkpoint(), c => c.Call(new BsonDocument { ["op"] = "checkpoint" }).AsInt32, false);

        public long Rebuild(RebuildOptions options) =>
            throw new NotSupportedException("Rebuild is not supported by the experimental coordinator; open the file in direct mode.");

        #endregion

        public void Dispose()
        {
            lock (_roleLock)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _host?.Dispose();
                _client?.Dispose();
                _host = null;
                _client = null;
            }
        }
    }
}
#endif
