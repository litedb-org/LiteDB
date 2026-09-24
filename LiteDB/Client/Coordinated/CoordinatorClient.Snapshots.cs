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
    /// status page shows the same coordinator and read version, without any round trip.
    /// A new snapshot is opened without asking the coordinator: register a lease for the
    /// published version, open, and accept only if no structural change overlapped (see
    /// docs/experimental-coordinator.md for the argument). Otherwise, or without a status
    /// page, the coordinator grants one over IPC. An idle cached snapshot is released so
    /// its lease cannot hold back WAL reclamation indefinitely.
    /// </summary>
    internal sealed partial class CoordinatorClient
    {
        private static readonly TimeSpan SnapshotIdle = TimeSpan.FromMilliseconds(500);
        private const int HandshakeAttempts = 3;

        // Guarded by _leaseLock.
        private Snapshot _current;
        private Timer _idle;

        internal long PageHits;
        internal long Refreshes;
        internal long RefreshRejects;
        internal long HandshakeOpens;
        internal long HandshakeRejects;
        internal long GrantOpens;

#if DEBUG || TESTING
        /// <summary>Test hook: runs at each handshake step ("page-read", "lease-registered", "engine-opened").</summary>
        internal Action<string> HandshakeStage;

        /// <summary>Negative-control switch: accept a snapshot without re-reading the page after opening it.</summary>
        internal bool UnsafeSkipRecheck;

        /// <summary>Negative-control switch: advance a snapshot although reclaimed slots were reused since its scan.</summary>
        internal bool UnsafeIgnoreReuse;

        /// <summary>Debug mode: compare every advanced snapshot with a freshly opened one at the same version.</summary>
        internal bool VerifyRefresh;

        internal long VerifiedRefreshes;
#endif

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

        /// <summary>Caller holds <c>_leaseLock</c>.</summary>
        private Snapshot AcquireSnapshot()
        {
            // Dispose unmaps the page under the same lock.
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CoordinatorClient));
            if (_page != null && _page.TryRead(out var status) && status.Instance != 0)
            {
                var current = _current;
                // Same coordinator and version: nothing committed since. The cached
                // snapshot's lease also rules out a WAL truncation (Resets) meanwhile.
                if (current != null && current.Instance == status.Instance && current.Resets == status.Resets &&
                    current.Version == status.Version)
                {
                    Interlocked.Increment(ref PageHits);
                    current.LastUse = DateTime.UtcNow;
                    return current;
                }
                if (current != null && this.TryRefresh(current, status))
                {
                    Interlocked.Increment(ref Refreshes);
                    current.LastUse = DateTime.UtcNow;
                    return current;
                }
                for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
                {
                    var opened = this.TryHandshake(status);
                    if (opened != null) return this.Install(opened, ref HandshakeOpens);
                    Interlocked.Increment(ref HandshakeRejects);
                    if (!_page.TryRead(out status) || status.Instance == 0) break;
                }
            }
            return this.GrantOverIpc();
        }

        /// <summary>
        /// Open a snapshot at the published version without the coordinator. Accepted only
        /// if the page shows the same coordinator, an even (unchanged) structural counter and
        /// no WAL reset after the open, and the engine opened at exactly that version.
        /// </summary>
        private Snapshot TryHandshake(CoordinatorStatus before)
        {
            if (!before.Quiet) return null;
            this.Stage("page-read");
            var version = checked((int)before.Version);
            IDisposable lease;
            try { lease = _registry.RegisterUnscanned(version); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // The lease file exists before the page is re-read (see the proof).
            Interlocked.MemoryBarrier();
            this.Stage("lease-registered");
            LiteEngine engine = null;
            try
            {
                engine = this.OpenSnapshotEngine();
                this.Stage("engine-opened");
                Interlocked.MemoryBarrier();
                if (this.Rechecked(before) && engine.ReadVersion == version)
                {
                    var accepted = new Snapshot(engine, lease, version, before);
                    engine = null;
                    lease = null;
                    return accepted;
                }
                return null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // A concurrent commit can tear the frame being written: the read-only
            // recovery then stops early, which the version check above rejects.
            catch (LiteException) { return null; }
            finally
            {
                engine?.Dispose();
                lease?.Dispose();
            }
        }

        /// <summary>
        /// Advance an idle cached snapshot by reading only the WAL frames appended since
        /// its last scan. Allowed while the page shows the same coordinator, structural
        /// counter, WAL resets and slot-reuse epoch as at that scan: then every newer frame
        /// was appended after it. A failed attempt may leave the engine partially advanced,
        /// so the snapshot is retired and a new one opened.
        /// </summary>
        private bool TryRefresh(Snapshot current, CoordinatorStatus status)
        {
            if (current.Readers != 0 || current.Retired || !status.Quiet || status.Version <= current.Version ||
                status.Instance != current.Instance || status.Structural != current.Structural ||
                status.Resets != current.Resets || !SameReuse(status, current)) return false;
            var version = checked((int)status.Version);
            IDisposable lease;
            try { lease = _registry.RegisterUnscanned(version); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            Interlocked.MemoryBarrier();
            this.Stage("refresh-lease");
            var advanced = false;
            try
            {
                var reached = current.Engine.AdvanceSnapshot();
                this.Stage("refreshed");
                Interlocked.MemoryBarrier();
                advanced = reached == version && this.Rechecked(status);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (LiteException) { }
            if (!advanced)
            {
                Interlocked.Increment(ref RefreshRejects);
                lease.Dispose();
                _current = null;
                Retire(current);
                return false;
            }
            current.Advance(version, lease);
            this.VerifyAdvanced(current);
            return true;
        }

        private bool SameReuse(CoordinatorStatus status, Snapshot current)
        {
#if DEBUG || TESTING
            if (UnsafeIgnoreReuse) return true;
#endif
            return status.ReuseEpoch == current.ReuseEpoch;
        }

        private void VerifyAdvanced(Snapshot snapshot)
        {
#if DEBUG || TESTING
            if (!VerifyRefresh) return;
            using var fresh = this.OpenSnapshotEngine();
            // Only comparable while nothing newer was committed; the new lease protects the version.
            if (fresh.ReadVersion != snapshot.Version) return;
            var expected = fresh.DescribeSnapshot();
            var actual = snapshot.Engine.DescribeSnapshot();
            if (expected != actual)
                throw new InvalidOperationException("An advanced coordinator snapshot differs from a fresh open:" +
                    Environment.NewLine + actual + Environment.NewLine + expected);
            Interlocked.Increment(ref VerifiedRefreshes);
#endif
        }

        private bool Rechecked(CoordinatorStatus before)
        {
#if DEBUG || TESTING
            if (UnsafeSkipRecheck) return true;
#endif
            return _page.TryRead(out var after) && after.Instance == before.Instance &&
                after.Structural == before.Structural && after.Resets == before.Resets;
        }

        private Snapshot GrantOverIpc()
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
            // The gate is closed, so the page is stable: record its identity for the fast path.
            var status = default(CoordinatorStatus);
            var published = _page != null && _page.TryRead(out status) && status.Version == version;
            try
            {
                // Lease first: the file outlives the coordinator, which may die now.
                lease = _registry.Register(version);
                engine = this.OpenSnapshotEngine();
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
            // Without a matching page the fast path and refresh never apply to it.
            return this.Install(new Snapshot(engine, lease, version, published ? status : default), ref GrantOpens);
        }

        private LiteEngine OpenSnapshotEngine()
        {
            var settings = _settings.Clone();
            settings.ReadOnly = true;
            settings.Upgrade = false;
            settings.AutoRebuild = false;
            settings.SharedReadSnapshot = true;
            return new LiteEngine(settings);
        }

        private Snapshot Install(Snapshot snapshot, ref long counter)
        {
            Retire(_current);
            _current = snapshot;
            Interlocked.Increment(ref SnapshotOpens);
            Interlocked.Increment(ref counter);
            _idle ??= new Timer(this.OnIdle, null, SnapshotIdle, SnapshotIdle);
            return snapshot;
        }

        private void Stage(string name)
        {
#if DEBUG || TESTING
            HandshakeStage?.Invoke(name);
#endif
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
            /// <param name="opened">The page before the engine scanned the WAL; default without a page.</param>
            internal Snapshot(LiteEngine engine, IDisposable lease, int version, CoordinatorStatus opened)
            {
                this.Engine = engine;
                this.Lease = lease;
                this.Version = version;
                this.Instance = opened.Instance;
                this.Structural = opened.Structural;
                this.ReuseEpoch = opened.ReuseEpoch;
                this.Resets = opened.Resets;
                this.LastUse = DateTime.UtcNow;
            }

            internal LiteEngine Engine { get; }
            internal IDisposable Lease { get; private set; }
            internal int Version { get; private set; }
            // Zero when the page was unavailable: the fast path and refresh never match it.
            internal long Instance { get; }
            internal long Structural { get; }
            internal long ReuseEpoch { get; }
            internal long Resets { get; }

            /// <summary>Move to a newer version, whose lease was registered before the advance.</summary>
            internal void Advance(int version, IDisposable lease)
            {
                var previous = this.Lease;
                this.Version = version;
                this.Lease = lease;
                previous.Dispose();
            }
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
