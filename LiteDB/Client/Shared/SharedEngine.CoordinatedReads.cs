#if NET8_0_OR_GREATER
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private readonly SharedSnapshotCache _readCache = new SharedSnapshotCache();
        private object _snapshotGate => _readCache.Gate;
        private SharedMemoryReadAdmission MappedReads => (_readers as SharedMemoryReaders)?.Table?.Reads;
        private SharedCoordinationPage _coordination;
        private bool _coordinationUnavailable;
        private readonly bool _mappedReadsDisabled = SharedCoordinationPolicy.MappedReadsDisabled;
        private int _coordinationDemand;
        private int _readCacheDemand { get => _readCache.Demand; set => _readCache.Demand = value; }
        private CachedSharedSnapshot _cachedSnapshot { get => _readCache.Snapshot; set => _readCache.Snapshot = value; }
        private Timer _snapshotIdle { get => _readCache.Idle; set => _readCache.Idle = value; }
        private SharedReadPacer _readPacer => _readCache.Pacer;

#if DEBUG || TESTING
        internal TimeSpan CoordinatedIdleLimit { get => _readCache.IdleLimit; set => _readCache.IdleLimit = value; }
        internal bool HasCachedSnapshot { get { lock (_snapshotGate) return _cachedSnapshot != null; } }
        internal int CoordinatedReadHits;
        internal int MeasuredStreamingReaders;
        internal System.Runtime.InteropServices.Architecture? CoordinationArchitectureOverride;
        internal Action<string> CoordinationStage;
        internal bool UnsafeSkipCoordinationRecheck;
        internal bool ForceCoordinatedYield;
#else
        private TimeSpan CoordinatedIdleLimit => _readCache.IdleLimit;
#endif

        private void EnsureReadCoordination()
        {
            if (_settings.SharedOneFile) return;
            // The first read already owns the database mutex and cannot retain a
            // cached snapshot. It needs neither an authority nor a filesystem probe.
            // Writable opens still discover an existing authority before mutation.
            if (_coordination == null && _coordinationDemand == 0)
                _coordinationDemand = 1;
            else
                this.EnsureCoordination();
        }

        // The caller owns the database mutex, so nobody can create a competing authority.
        private void EnsureCoordination(bool allowCreate = true, bool writing = false)
        {
            if (_settings.SharedOneFile) return; // ABI 2 uses the participant authority.
            if (_coordination != null) return;
            if (!SharedCoordinationFallback.SupportsNames(_settings.Filename))
            {
                _coordinationUnavailable = true;
                CoordinationFallbackReason = "names: control paths exceed the supported limit";
                return;
            }
            // One-shot connections keep their existing lifecycle. Repeated operations
            // create an authority; every later writer must join one that already exists.
            if ((!allowCreate || ++_coordinationDemand < 2) &&
                !SharedCoordinationRevocation.ExistsOrUnknown(SharedCoordinationPage.PagePath(_settings.Filename))) return;
            if (_mappedReadsDisabled)
            {
                _coordinationUnavailable = true;
                CoordinationFallbackReason = "disabled: " + SharedCoordinationPolicy.DisableMappedSwitch;
            }
            if (_coordinationUnavailable)
            {
                if (writing) SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
                return;
            }
            var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
#if DEBUG || TESTING
            architecture = this.CoordinationArchitectureOverride ?? architecture;
#endif
            if (architecture != System.Runtime.InteropServices.Architecture.X64 &&
                architecture != System.Runtime.InteropServices.Architecture.X86 &&
                architecture != System.Runtime.InteropServices.Architecture.Arm64)
            {
                CoordinationFallbackReason = "architecture: " + architecture;
                if (writing) SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
                _coordinationUnavailable = true;
                return;
            }
            if (!this.CanScope || _settings.Filename == ":memory:" || _settings.Filename == ":temp:" ||
                (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows) && _handles == null))
            {
                CoordinationFallbackReason = "settings: " + this.CanScope + "/" + _settings.Filename;
                if (writing) SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
                _coordinationUnavailable = true;
                return;
            }
            try
            {
                // Unknown and remote volumes retain the existing protocol. The first
                // prototype is deliberately narrow; platform qualification is separate.
                var volumeFailure = SharedCoordinationPolicy.VolumeFailure(_settings.Filename);
                if (volumeFailure != null)
                {
                    CoordinationFallbackReason = volumeFailure;
                    if (writing) SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
                    _coordinationUnavailable = true;
                    return;
                }
                _coordination = SharedCoordinationFile.RetrySharingViolation(() => SharedCoordinationPage.Open(_settings.Filename));
                _settings.CoordinationSignals = _coordination;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException)
            {
                // Existing participants must learn about this fallback before this
                // connection is allowed to make a write they would otherwise miss.
                CoordinationFallbackReason = error.ToString();
                if (writing) SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
                _coordinationUnavailable = true;
            }
        }

        private IBsonDataReader TryQueryCoordinated(string collection, Query query)
        {
            if ((_coordination == null && MappedReads == null) || _pin != null || _transactionRunning || _owner.IsOwnedByCurrentThread) return null;
            bool yieldToWriter;
            int delay;
            lock (_snapshotGate)
            {
                yieldToWriter = _cachedSnapshot != null && (MappedReads?.ShouldYieldToWriter(Environment.TickCount64) ??
                    (_coordination?.ShouldYieldToWriter(Environment.TickCount64) ?? false));
#if DEBUG || TESTING
                yieldToWriter |= ForceCoordinatedYield;
#endif
                delay = _readPacer.ReserveDelay(yieldToWriter);
            }
            if (yieldToWriter)
            {
#if DEBUG || TESTING
                CoordinationStage?.Invoke("writer-pressure");
#endif
                // A scheduling delay must not extend a reader lease or hold the
                // local lifetime gate. Re-read all admission state afterwards.
                var started = Stopwatch.GetTimestamp();
                if (delay != 0) Thread.Sleep(delay);
                else Thread.Yield();
                lock (_snapshotGate) _readPacer.RecordDelay(delay, Stopwatch.GetTimestamp() - started);
            }
            var workStarted = yieldToWriter ? Stopwatch.GetTimestamp() : 0;
            CachedSharedSnapshot snapshot;
            lock (_snapshotGate)
            {
                snapshot = _cachedSnapshot;
                if (snapshot == null || !this.TryReadStatus(out var status, hint: true) ||
                    status.Version != snapshot.Status.Version || !status.SameStorage(snapshot.Status)) return null;
#if DEBUG || TESTING
                CoordinationStage?.Invoke("cached-status");
#endif
                var addedLease = snapshot.Lease == null;
                if (addedLease)
                {
                    try { snapshot.Lease = _readers.RegisterUnscanned(checked((int)status.Version)); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return null; }
                }
                try
                {
                    Interlocked.MemoryBarrier();
                    if (_settings.SharedOneFile) SharedMemoryReadAdmission.Stage("reader-record");
#if DEBUG || TESTING
                    CoordinationStage?.Invoke("lease-published");
                    if (!UnsafeSkipCoordinationRecheck)
#endif
                    {
                        if (!this.TryReadStatus(out var after) || !after.SameStorage(status) ||
                            (MappedReads != null && (after.Sequence != status.Sequence || after.Version != status.Version)))
                        {
                            if (addedLease) { snapshot.Lease?.Dispose(); snapshot.Lease = null; }
                            return null;
                        }
                    }
                    lock (_useLock) this.AdmitLocked();
                }
                catch
                {
                    if (addedLease) { snapshot.Lease?.Dispose(); snapshot.Lease = null; }
                    throw;
                }
                snapshot.Readers++;
#if DEBUG || TESTING
                Interlocked.Increment(ref CoordinatedReadHits);
#endif
            }
            return this.ReadCached(collection, query, snapshot, workStarted);
        }

        /// <summary>Install only a snapshot opened and leased under the database mutex.</summary>
        private IBsonDataReader TryInstallCoordinated(string collection, Query query, LiteEngine engine)
        {
            // A single read between writes cannot amortize retaining a snapshot.
            // Start caching only after two consecutive read-only opens.
            SharedCoordinationStatus status;
            IDisposable lease;
            try
            {
                if (_readCacheDemand < 2) _readCacheDemand++;
                if (_readCacheDemand < 2 || !this.TryReadStatus(out status) || status.Version != engine.ReadVersion) return null;
                lease = this.TryRegisterLease(engine.ReadVersion);
                if (lease == null) return null;
            }
            catch
            {
                try { engine.Dispose(); }
                finally { _owner.Exit(); }
                throw;
            }
            var snapshot = new CachedSharedSnapshot(engine, lease, status) { Readers = 1 };
            var installed = false;
            try
            {
                lock (_snapshotGate)
                {
                    if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SharedEngine));
                    var previous = _cachedSnapshot;
                    _cachedSnapshot = null;
                    RetireSnapshot(previous);
                    _cachedSnapshot = snapshot;
#if DEBUG || TESTING
                    _readCache.Observer = new WeakReference<SharedEngine>(this);
#endif
                    _readCache.StartExpiry();
                    installed = true;
                }
                return this.ReadCached(collection, query, snapshot);
            }
            catch
            {
                if (!installed)
                {
                    lock (_snapshotGate)
                        if (ReferenceEquals(snapshot, _cachedSnapshot)) _cachedSnapshot = null;
                    snapshot.Close();
                }
                throw;
            }
            finally { _owner.Exit(); }
        }

        private void RecordCachedWork(long ticks)
        {
            lock (_snapshotGate) _readPacer.RecordWork(ticks);
        }

        private IBsonDataReader ReadCached(string collection, Query query, CachedSharedSnapshot snapshot, long workStarted = 0)
        {
            IBsonDataReader reader = null;
            int? local = null;
            var released = false;
            try
            {
#if DEBUG || TESTING
                CoordinationStage?.Invoke("admitted");
#endif
                reader = snapshot.Engine.Query(collection, query);
                var buffered = TryBuffer(reader, out var prefix);
                if (buffered != null)
                {
                    reader.Dispose();
                    reader = null;
                    released = true;
                    this.ReleaseCached(snapshot, null);
                    return buffered;
                }
                local = this.AddLocalReader();
                lock (_snapshotGate) snapshot.HadStreamingReader = true;
                IBsonDataReader continued = new PrefixedDataReader(prefix, reader);
                if (workStarted != 0)
                {
                    continued = new MeasuredSharedReader(continued, this.RecordCachedWork);
#if DEBUG || TESTING
                    Interlocked.Increment(ref MeasuredStreamingReaders);
#endif
                }
                return new SharedDataReader(continued, () => this.ReleaseCached(snapshot, local));
            }
            catch
            {
                try { reader?.Dispose(); }
                finally
                {
                    lock (_snapshotGate)
                    {
                        snapshot.Retired = true;
                        if (ReferenceEquals(snapshot, _cachedSnapshot)) _cachedSnapshot = null;
                    }
                    if (!released) this.ReleaseCached(snapshot, local);
                }
                throw;
            }
            finally { if (workStarted != 0) this.RecordCachedWork(Stopwatch.GetTimestamp() - workStarted); }
        }

        private void ReleaseCached(CachedSharedSnapshot snapshot, int? local)
        {
            var checkpoint = false;
            try
            {
                lock (_snapshotGate)
                {
                    snapshot.Readers--;
                    snapshot.LastUse = Environment.TickCount64;
                    if (snapshot.Readers == 0)
                    {
                        // Idle cache state is untrusted and cannot delay checkpointing.
                        // The next use republishes a lease and validates its storage fence.
                        snapshot.Lease?.Dispose();
                        snapshot.Lease = null;
                        if (!snapshot.Engine.CanRetainSharedSnapshot)
                        {
                            snapshot.Retired = true;
                            if (ReferenceEquals(snapshot, _cachedSnapshot)) _cachedSnapshot = null;
                        }
                        checkpoint = snapshot.HadStreamingReader;
                        snapshot.HadStreamingReader = false;
                        if (snapshot.Retired) snapshot.Close();
                        if (_cachedSnapshot == null) { _snapshotIdle?.Dispose(); _snapshotIdle = null; }
                    }
                }
            }
            finally
            {
                if (local.HasValue) this.RemoveLocalReader(local.Value);
                else if (checkpoint) this.CheckpointAfterLastReader();
            }
        }

        private void ExpireSnapshot() => _readCache.Expire();

        private void RetireCachedSnapshot()
        {
            lock (_snapshotGate)
            {
                _snapshotIdle?.Dispose();
                _snapshotIdle = null;
                RetireSnapshot(_cachedSnapshot);
                _cachedSnapshot = null;
            }
        }

        private static void RetireSnapshot(CachedSharedSnapshot snapshot)
        {
            if (snapshot == null) return;
            snapshot.Retired = true;
            if (snapshot.Readers == 0) snapshot.Close();
        }

        private bool TryReadStatus(out SharedCoordinationStatus status, bool hint = false)
        {
            status = default;
            var mapped = MappedReads;
            if (mapped != null) return !_mappedReadsDisabled && mapped.TryRead(out status);
            return _coordination != null && (hint ? _coordination.TryReadHint(out status) : _coordination.TryRead(out status));
        }
    }
}
#endif
