using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private const int BUFFERED_RESULT_VALUES = 100;
        private const int BUFFERED_RESULT_BYTES = 64 * 1024;

        /// <summary>
        /// Open a streaming snapshot. A result that fits the buffer budget completes
        /// under the mutex instead, without a second engine or a lease. Ordinary readers retain a process-lifetime
        /// lease and release the writer mutex before returning to the caller.
        /// </summary>
        public IBsonDataReader Query(string collection, Query query)
        {
            var reads = query?.ForUpdate != true && query?.Into == null;
            SharedMutexPin use;
            if (reads && _pin == null)
            {
                // The same acquisition as OpenDatabase. Where it would open the writable
                // operation engine, a pure read opens the read-only snapshot engine instead.
                var recoveredAbandonedOwner = _owner.Enter();
                try { RejectAbandonedTransaction(); }
                catch { _owner.Exit(); throw; }
                if (!_transactionRunning && _engine == null)
                {
                    var readOnly = this.TryOpenSnapshot(recoveredAbandonedOwner);
                    if (readOnly != null) return this.QuerySnapshot(collection, query, readOnly);
                    // The file needs a writable open first (creation, upgrade, index
                    // migration, format promotion, auto-rebuild): proceed as before.
                    try { this.OpenEngine(recoveredAbandonedOwner); }
                    catch { _owner.Exit(); throw; }
                }
                lock (_useLock) _databaseUsers++;
                use = null;
            }
            else use = this.OpenDatabase();

            // Write queries and explicit transactions retain their writer ownership.
            if (_transactionRunning || !reads)
            {
                return this.QueryUnderMutex(collection, query, use);
            }

            LiteEngine snapshot = null;
            IDisposable lease = null;
            var closeDatabase = true;
            try
            {
                // A user callback can be stateful. Speculative buffering followed
                // by snapshot replay would execute it twice for the discarded
                // prefix, so transformed queries always take the one-pass path.
                if (_settings.ReadTransform == null)
                {
                    var buffered = this.TryBufferResult(collection, query);
                    if (buffered != null) return buffered;
                }

                // Replay and registration are ordered with commits/checkpoints by
                // the mutex. This engine's index never changes for the query lifetime.
                lease = this.TryRegisterLease();
                if (lease == null)
                {
                    // No lease can protect a snapshot (for example, a read-only
                    // directory). Stream under the mutex, as before v13.
                    closeDatabase = false;
                    return this.QueryUnderMutex(collection, query, use);
                }
                var settings = _settings.Clone();
                settings.ReadOnly = true;
                settings.Upgrade = false;
                settings.AutoRebuild = false;
                settings.SharedReadSnapshot = true;
                snapshot = new LiteEngine(settings);
                var reader = snapshot.Query(collection, query);
                var ownedSnapshot = snapshot;
                var ownedLease = lease;
                var owner = this.AddLocalReader();
                var result = new SharedDataReader(reader, () =>
                {
                    try { ownedSnapshot.Dispose(); }
                    finally
                    {
                        try { ownedLease.Dispose(); }
                        finally { this.RemoveLocalReader(owner); }
                    }
                });
                snapshot = null;
                lease = null;
                return result;
            }
            finally
            {
                try { snapshot?.Dispose(); }
                finally
                {
                    try { lease?.Dispose(); }
                    finally { if (closeDatabase) this.CloseDatabase(use); }
                }
            }
        }

        /// <summary>
        /// Stream from this process' engine while retaining the mutex (or the
        /// caller's pin) until the reader is disposed. The caller has opened the database.
        /// </summary>
        private IBsonDataReader QueryUnderMutex(string collection, Query query, SharedMutexPin use)
        {
            try
            {
                var reader = _engine.Query(collection, query);
                use?.ToHold();
                // Any thread may dispose the reader and so end its mutex ownership.
                var generation = use == null ? _owner.Generation : -1;
                return new SharedDataReader(reader, () => this.CloseDatabase(use, hold: true, generation));
            }
            catch
            {
                this.CloseDatabase(use);
                throw;
            }
        }

        private IDisposable TryRegisterLease() => this.TryRegisterLease(_engine.ReadVersion);

        private IDisposable TryRegisterLease(int version)
        {
            try
            {
                return _readers.Register(version);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// A pure read while no engine of this connection is open. It runs on a read-only
        /// snapshot engine opened under the mutex, the same configuration a leased reader
        /// always used (private sort space, never writes or deletes the WAL). A result
        /// within the buffer budget completes under the mutex. A larger one registers a
        /// lease for exactly this engine's read version before the mutex is released and
        /// continues the same reader, so the query is not executed a second time.
        /// The caller owns one mutex recursion, which this method releases or hands over.
        /// </summary>
        private IBsonDataReader QuerySnapshot(string collection, Query query, LiteEngine snapshot)
        {
            IBsonDataReader reader = null;
            IDisposable lease = null;
            var release = true;
            try
            {
                reader = snapshot.Query(collection, query);

                List<BsonValue> prefix = null;
                // A stateful user callback must not see a discarded prefix; with one the
                // snapshot streams in one pass, as before.
                if (_settings.ReadTransform == null)
                {
                    var buffered = TryBuffer(reader, out prefix);
                    if (buffered != null) return buffered;
                }
                IBsonDataReader continued = prefix == null ? reader : new PrefixedDataReader(prefix, reader);

                lease = this.TryRegisterLease(snapshot.ReadVersion);
                if (lease == null)
                {
                    // No lease can protect the snapshot (for example, a read-only
                    // directory). Stream under the mutex, as before v13.
                    var generation = _owner.Generation;
                    var locked = snapshot;
                    lock (_useLock) _mutexSnapshots.Add(locked);
                    snapshot = null;
                    reader = null;
                    release = false;
                    return new SharedDataReader(continued, () =>
                    {
                        try { this.CloseMutexSnapshot(locked); }
                        finally { _owner.Exit(generation); }
                    });
                }

                var ownedSnapshot = snapshot;
                var ownedLease = lease;
                var owner = this.AddLocalReader();
                var result = new SharedDataReader(continued, () =>
                {
                    try { ownedSnapshot.Dispose(); }
                    finally
                    {
                        try { ownedLease.Dispose(); }
                        finally { this.RemoveLocalReader(owner); }
                    }
                });
                snapshot = null;
                lease = null;
                reader = null;
                return result;
            }
            finally
            {
                try { reader?.Dispose(); }
                finally
                {
                    try { snapshot?.Dispose(); }
                    finally
                    {
                        try { lease?.Dispose(); }
                        finally
                        {
                            if (release)
                            {
                                if (!_transactionRunning) _transactionThreadId = 0;
                                _owner.Exit();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Open the read-only snapshot engine for a pure read, or return null when this
        /// file needs the writable operation engine first: a read-only open that fails
        /// (missing or empty file, pending upgrade, index migration or format promotion)
        /// wrote nothing, and an invalid-state header must reach AutoRebuild.
        /// </summary>
        private LiteEngine TryOpenSnapshot(bool recoveredAbandonedOwner)
        {
            LiteEngine snapshot;
            try
            {
                snapshot = this.CreateEngine(recoveredAbandonedOwner, this.SnapshotSettings());
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return null;
            }
            if (_settings.AutoRebuild && snapshot.InvalidDatafileState)
            {
                snapshot.Dispose();
                return null;
            }
#if DEBUG || TESTING
            this.SnapshotOpens++;
#endif
            _recoveryReport = snapshot.RecoveryReport ?? _recoveryReport;
            snapshot.RecoveryReport = _recoveryReport;
            return snapshot;
        }

        private EngineSettings SnapshotSettings()
        {
            var settings = _settings.Clone();
            // A writable connection migrates legacy index ordering on its writable open;
            // only a read-only connection may choose to scan instead.
            settings.LegacyIndexScan = _settings.ReadOnly && _settings.LegacyIndexScan;
            settings.ReadOnly = true;
            settings.Upgrade = false;
            settings.AutoRebuild = false;
            settings.SharedReadSnapshot = true;
            return settings;
        }

        /// <summary>
        /// Close a snapshot that streamed under the mutex, unless the connection's
        /// Dispose or an exited mutex owner already closed it.
        /// </summary>
        private void CloseMutexSnapshot(LiteEngine snapshot)
        {
            lock (_useLock)
            {
                if (!_mutexSnapshots.Remove(snapshot)) return;
            }
            snapshot.Dispose();
        }

        /// <summary>
        /// Read <paramref name="reader"/> to its end within the buffer budget and return the
        /// buffered result. Returns null when it exceeds the budget: <paramref name="prefix"/>
        /// then holds the buffered values and the reader is positioned on the first value
        /// that did not fit.
        /// </summary>
        private static IBsonDataReader TryBuffer(IBsonDataReader reader, out List<BsonValue> prefix)
        {
            var values = new List<BsonValue>();
            var bytes = 0;
            prefix = null;
            try
            {
                while (reader.Read())
                {
                    if (values.Count == BUFFERED_RESULT_VALUES) { prefix = values; return null; }
                    bytes += reader.Current.GetBytesCount(true);
                    if (bytes > BUFFERED_RESULT_BYTES) { prefix = values; return null; }
                    values.Add(reader.Current);
                }
            }
            catch (Exception ex) when (values.Count > 0)
            {
                // A streaming reader fails at the row that cannot be produced,
                // after yielding the rows before it. Keep that contract.
                return new BufferedDataReader(values, reader.Collection, ExceptionDispatchInfo.Capture(ex));
            }
            return new BufferedDataReader(values, reader.Collection);
        }

        /// <summary>
        /// Read a small result to its end while this process owns the mutex.
        /// Returns null when it exceeds the budget; the caller then streams it
        /// from a leased snapshot of the same committed state.
        /// </summary>
        private IBsonDataReader TryBufferResult(string collection, Query query)
        {
            var values = new List<BsonValue>();
            var bytes = 0;
            using (var reader = _engine.Query(collection, query))
            {
                try
                {
                    while (reader.Read())
                    {
                        if (values.Count == BUFFERED_RESULT_VALUES) return null;
                        bytes += reader.Current.GetBytesCount(true);
                        if (bytes > BUFFERED_RESULT_BYTES) return null;
                        values.Add(reader.Current);
                    }
                }
                catch (Exception ex) when (values.Count > 0)
                {
                    // A streaming reader fails at the row that cannot be produced,
                    // after yielding the rows before it. Keep that contract.
                    return new BufferedDataReader(values, reader.Collection, ExceptionDispatchInfo.Capture(ex));
                }
                return new BufferedDataReader(values, reader.Collection);
            }
        }
    }
}
