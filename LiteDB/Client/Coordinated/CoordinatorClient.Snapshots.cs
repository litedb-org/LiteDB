#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using LiteDB.Engine;
using static LiteDB.Client.Coordinated.CoordinatorProtocol;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Direct reads. The client keeps its latest snapshot engine and reuses it while the
    /// coordinator reports that nothing was committed since; otherwise it registers a new
    /// snapshot. An idle cached snapshot is released so its lease cannot hold back WAL
    /// reclamation indefinitely.
    /// </summary>
    internal sealed partial class CoordinatorClient
    {
        private static readonly TimeSpan SnapshotIdle = TimeSpan.FromMilliseconds(500);

        // Guarded by _leaseLock.
        private Snapshot _current;
        private Timer _idle;

        internal bool HasCachedSnapshot
        {
            get { lock (_leaseLock) return _current != null; }
        }

        /// <summary>
        /// Query a direct read-only snapshot, or return null when the coordinator is too
        /// busy to grant one quickly or the lease cannot be registered.
        /// </summary>
        internal IBsonDataReader TryQueryDirect(string collection, Query query)
        {
            Snapshot snapshot;
            lock (_leaseLock)
            {
                snapshot = this.AcquireSnapshot();
                if (snapshot == null) return null;
                snapshot.Readers++;
            }
            Interlocked.Increment(ref DirectReads);
            try
            {
                var reader = snapshot.Engine.Query(collection, query);
                return new SharedDataReader(reader, () => this.Release(snapshot));
            }
            catch
            {
                this.Release(snapshot);
                throw;
            }
        }

        private Snapshot AcquireSnapshot()
        {
            var stream = this.GetLeaseStream();
            BsonDocument grant;
            try
            {
                Write(stream, new BsonDocument { ["op"] = "snapshot", ["have"] = _current?.Version ?? -1 });
                grant = Result(Read(stream)).AsDocument;
            }
            catch (Exception ex) when (IsPipeFailure(ex))
            {
                this.DropLeaseStream();
                throw new CoordinatorLostException(ex);
            }
            if (grant.ContainsKey("same"))
            {
                _current.LastUse = DateTime.UtcNow;
                return _current;
            }
            if (grant.ContainsKey("busy")) return null;

            var version = grant["v"].AsInt32;
            IDisposable lease = null;
            LiteEngine engine = null;
            var opened = false;
            try
            {
                // Lease first: the file outlives the coordinator, which may die now.
                lease = _registry.Register(version);
                var settings = _settings.Clone();
                settings.ReadOnly = true;
                settings.Upgrade = false;
                settings.AutoRebuild = false;
                settings.SharedReadSnapshot = true;
                engine = new LiteEngine(settings);
                if (engine.ReadVersion != version)
                    throw new LiteException(0, $"Coordinated snapshot opened at version {engine.ReadVersion}, expected {version}.");
                opened = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                // The coordinator keeps its gate closed until this answer.
                try { Write(stream, new BsonDocument { ["op"] = opened ? "opened" : "abort" }); }
                catch (Exception ex) when (IsPipeFailure(ex)) { this.DropLeaseStream(); }
                if (!opened)
                {
                    engine?.Dispose();
                    lease?.Dispose();
                }
            }
            if (!opened) return null;
            Retire(_current);
            _current = new Snapshot(engine, lease, version);
            Interlocked.Increment(ref SnapshotOpens);
            _idle ??= new Timer(this.OnIdle, null, SnapshotIdle, SnapshotIdle);
            return _current;
        }

        private void Release(Snapshot snapshot)
        {
            lock (_leaseLock)
            {
                snapshot.Readers--;
                snapshot.LastUse = DateTime.UtcNow;
                if (snapshot.Retired && snapshot.Readers == 0) snapshot.Close();
            }
        }

        private void OnIdle(object state)
        {
            lock (_leaseLock)
            {
                var snapshot = _current;
                if (snapshot == null || snapshot.Readers > 0 || DateTime.UtcNow - snapshot.LastUse < SnapshotIdle) return;
                _current = null;
                Retire(snapshot);
            }
        }

        /// <summary>Caller holds <c>_leaseLock</c>. Open readers close their snapshot when they finish.</summary>
        private void CloseSnapshots()
        {
            _idle?.Dispose();
            _idle = null;
            Retire(_current);
            _current = null;
        }

        private static void Retire(Snapshot snapshot)
        {
            if (snapshot == null) return;
            snapshot.Retired = true;
            if (snapshot.Readers == 0) snapshot.Close();
        }

        private sealed class Snapshot
        {
            internal Snapshot(LiteEngine engine, IDisposable lease, int version)
            {
                this.Engine = engine;
                this.Lease = lease;
                this.Version = version;
                this.LastUse = DateTime.UtcNow;
            }

            internal LiteEngine Engine { get; }
            internal IDisposable Lease { get; }
            internal int Version { get; }
            internal int Readers;
            internal bool Retired;
            internal DateTime LastUse;
            private bool _closed;

            internal void Close()
            {
                if (_closed) return;
                _closed = true;
                try { this.Engine.Dispose(); }
                finally { this.Lease.Dispose(); }
            }
        }
    }
}
#endif
