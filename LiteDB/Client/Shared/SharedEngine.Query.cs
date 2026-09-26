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
            var use = this.OpenDatabase();
            // Write queries and explicit transactions retain their writer ownership.
            if (_transactionRunning || query?.ForUpdate == true || query?.Into != null)
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
                return new SharedDataReader(reader, () => this.CloseDatabase(use, hold: true));
            }
            catch
            {
                this.CloseDatabase(use);
                throw;
            }
        }

        private IDisposable TryRegisterLease()
        {
            try
            {
                return _readers.Register(_engine.ReadVersion);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
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
