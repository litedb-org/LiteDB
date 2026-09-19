using System;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        /// <summary>
        /// Open a streaming snapshot. Ordinary readers retain a process-lifetime
        /// lease and release the writer mutex before returning to the caller.
        /// </summary>
        public IBsonDataReader Query(string collection, Query query)
        {
            this.OpenDatabase();
            // Write queries and explicit transactions retain their writer ownership.
            if (_transactionRunning || query?.ForUpdate == true || query?.Into != null)
            {
                try
                {
                    return new SharedDataReader(_engine.Query(collection, query), this.CloseDatabase);
                }
                catch
                {
                    this.CloseDatabase();
                    throw;
                }
            }

            LiteEngine snapshot = null;
            IDisposable lease = null;
            try
            {
                // Replay and registration are ordered with commits/checkpoints by
                // the mutex. This engine's index never changes for the query lifetime.
                lease = _readers.Register(_engine.ReadVersion);
                var settings = _settings.Clone();
                settings.ReadOnly = true;
                settings.Upgrade = false;
                settings.AutoRebuild = false;
                settings.SharedReadSnapshot = true;
                snapshot = new LiteEngine(settings);
                var reader = snapshot.Query(collection, query);
                var ownedSnapshot = snapshot;
                var ownedLease = lease;
                var result = new SharedDataReader(reader, () =>
                {
                    try { ownedSnapshot.Dispose(); }
                    finally { ownedLease.Dispose(); }
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
                    finally { this.CloseDatabase(); }
                }
            }
        }
    }
}
