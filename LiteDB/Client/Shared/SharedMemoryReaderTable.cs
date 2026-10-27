#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Lifetime references are facade/result edges, never per call or per row.
    // Equal versions share one record. All distinct versions must remain visible.
    internal sealed class SharedMemoryReaderTable : IWriteAdmissionSignals
    {
        private readonly object _gate = new object();
        private readonly Dictionary<int, Generation> _versions = new Dictionary<int, Generation>();
        private readonly Stack<int> _free = new Stack<int>();
        internal readonly SharedMemoryAuthority Authority;
        internal readonly SharedLocalTransactionOwner LocalTransactions = new SharedLocalTransactionOwner();
        private readonly NativeAdmission _admission;
        internal NativeAdmission Admission => _admission;
        internal readonly EngineSettings Settings;
        internal readonly SharedSnapshotCache ReadCache = new SharedSnapshotCache();
        internal readonly SharedMemoryReadAdmission Reads;
        internal Action ClosedCallback;
        private int _references = 1;
        private bool _closing;
        private int _facades;
        private int _maintenanceDepth;
        internal void AttachFacade()
        {
            lock (_gate)
            {
                if (_maintenanceDepth != 0) throw new InvalidOperationException("Shared maintenance requires the only participant; retry after maintenance completes.");
                _facades++;
            }
        }
        internal void DetachFacade()
        {
            lock (_gate) _facades--;
            Release();
        }
        internal IDisposable Maintain(EngineSettings settings) => new SharedMemoryMaintenance(this, settings);
        public IDisposable TryEnterTruncate() => SharedMemoryTruncate.TryEnter(Authority, Settings.Filename);
        internal void BeginMaintenance()
        {
            lock (_gate)
            {
                if (_facades > 1 || _versions.Count != 0)
                    throw new InvalidOperationException("Shared maintenance requires the only participant. Close all other facades and readers first.");
                _maintenanceDepth++;
            }
        }
        internal bool EndMaintenance() { lock (_gate) return --_maintenanceDepth == 0; }
        private readonly SharedMemoryWriter _writer;
        internal bool Closed { get { lock (_gate) return _references == 0; } }
        internal bool HasReaders { get { lock (_gate) return _versions.Count != 0; } }

        private SharedMemoryReaderTable(SharedMemoryAuthority authority, NativeAdmission admission, EngineSettings settings)
        {
            Settings = settings;
            Authority = authority;
            _writer = new SharedMemoryWriter(authority);
            Reads = new SharedMemoryReadAdmission(authority);
            _admission = admission;
            for (var i = authority.Header.Generations - 1; i >= 0; i--) _free.Push(i);
        }

        internal static SharedMemoryReaderTable Open(EngineSettings settings)
        {
            var captured = settings.Clone();
            captured.SharedMode = true;
            // Retain only immutable opening choices, never facade callbacks/resources.
            captured.SharedReaderVersions = null;
            captured.AutoRebuildAllowed = null;
            captured.SharedMaintenance = null;
            captured.SharedAdmission = null;
            captured.SharedFileHandles = null;
            captured.CoordinationSignals = null;
            captured.DurableCommitPublication = null;
            captured.TransactionOwner = null;
            var admission = NativeAdmission.Open(captured, true);
            try
            {
                // Offline migration is deliberately not automatic in slices 0–4.
                // Never delete old/unknown files; the operator must stop old clients.
                if (Directory.Exists(captured.Filename + "-readers") ||
                    new[] { "-shared-live", "-shared-state", "-shared-disabled" }.Any(s => File.Exists(captured.Filename + s)))
                    throw new IOException("Stop all ABI-1 participants and remove recognized legacy coordination offline before enabling SharedOneFile.");
                SharedMemoryReaderTable table = null;
                SharedMemoryAuthority.Open(captured, admission.Identity, current =>
                {
                    using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(captured.Filename, captured.SharedMutexNameStrategy));
                    var held = false;
                    var abandoned = false;
                    try
                    {
                        try { held = main.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; abandoned = true; }
                        if (!held) throw new IOException("Protected Shared storage validation timed out.");
                        var coreSettings = captured.Clone();
                        coreSettings.SharedAdmission = admission;
                        table = new SharedMemoryReaderTable(current, admission, captured);
                        captured.TransactionOwner = table.LocalTransactions.Token;
                        coreSettings.TransactionOwner = table.LocalTransactions.Token;
                        coreSettings.SharedReaderVersions = table.LiveVersions;
                        coreSettings.AutoRebuildAllowed = () => true;
                        coreSettings.SharedMaintenance = () => table.Maintain(coreSettings);
                        // Upgrade=true authorizes a writable v7 maintenance open
                        // before the requested read-only facade, as in Direct.
                        // Probe under main; a no-op upgrade must retain read-only
                        // storage semantics for modern databases.
                        if (coreSettings.Upgrade && LiteEngine.RequiresUpgrade(coreSettings))
                        {
                            coreSettings.ReadOnly = false;
                            coreSettings.ReadOnlyStorage = false;
                        }
                        var writable = !coreSettings.ReadOnly && !coreSettings.ReadOnlyStorage;
                        LiteEngine core = null;
                        var publish = false;
                        if (writable) table._writer.Enter(borrowedWrite: current.Slot < 0, abandonedOwner: abandoned);
                        try
                        {
                            if (writable)
                            {
                                coreSettings.SharedWriterTenure = table.WriterTenure;
                                coreSettings.CoordinationSignals = table;
                                coreSettings.DurableCommitPublication = table.PublishDurableCommit;
                                table.BeginOpenRecovery();
                            }
                            core = new LiteEngine(coreSettings);
                            captured.Collation = new Collation(core.Pragma(Pragmas.COLLATION).AsString);
                            if (writable)
                            {
                                core.SyncSharedRecovery();
                                table.Opened(core.ReadVersion);
                            }
                            var errors = core.Close();
                            publish = !core.StorageUncertain && errors.Count == 0;
                        }
                        finally
                        {
                            try { core?.Close(checkpoint: false); }
                            finally { if (writable) table.ExitWriter(publish); }
                        }
                    }
                    finally { if (held) main.ReleaseMutex(); }
                });
                return table;
            }
            catch { admission.Dispose(); throw; }
        }

        internal bool TryRetain()
        {
            lock (_gate)
            {
                while (_closing) Monitor.Wait(_gate);
                if (_references == 0) return false;
                _references++;
                return true;
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                if (--_references != 0) return;
                _closing = true;
            }
            // No table gate while taking the cache gate: admission takes them in
            // the reverse order. Zero references means no facade or result remains.
            try
            {
                try
                {
                    ReadCache.Dispose();
                    SharedMemoryFinalClose.Run(this);
                }
                finally
                {
                    try { Authority.Dispose(); }
                    finally { try { _admission.Dispose(); } finally { ClosedCallback?.Invoke(); } }
                }
            }
            finally
            {
                lock (_gate) { _closing = false; Monitor.PulseAll(_gate); }
            }
        }

        internal IDisposable Register(int version)
        {
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
            lock (_gate)
            {
                if (_references == 0) throw new ObjectDisposedException(nameof(SharedMemoryReaderTable));
                RequireHeader(Authority);
                if (!_versions.TryGetValue(version, out var generation))
                {
                    if (_free.Count == 0) throw new SharedGenerationCapacityException();
                    generation = new Generation { Slot = _free.Pop() };
                    _versions.Add(version, generation);
                    var packed = (long)(((ulong)(uint)~version << 32) | (uint)version);
                    Authority.Map.Store(Authority.Block + SharedMemoryLayout.ReaderOffset + generation.Slot * 8L, packed);
                }
                generation.References++;
                _references++;
                return new Registration(this, version);
            }
        }

        internal IDisposable RequestWriter(Func<bool> cancelled = null)
        {
            if (!TryRetain()) throw new ObjectDisposedException(nameof(SharedMemoryReaderTable));
            try { return new WriterRequest(this, Authority.Requests.Enter(cancelled)); }
            catch { Release(); throw; }
        }
        private sealed class WriterRequest : IDisposable
        {
            private SharedMemoryReaderTable _table;
            private readonly IDisposable _request;
            internal WriterRequest(SharedMemoryReaderTable table, IDisposable request)
            { _table = table; _request = request; }
            public void Dispose()
            {
                var table = Interlocked.Exchange(ref _table, null);
                if (table == null) return;
                try { _request.Dispose(); }
                finally { table.Release(); }
            }
        }

        internal long WriterTenure => _writer.Tenure;
        public void BindState(EngineState state, long tenure) => _writer.BindState(state, tenure);
        public void Stopped(long tenure) => _writer.Stopped(tenure);
        internal void EnterWriter(bool abandonedOwner = false) => _writer.Enter(abandonedOwner: abandonedOwner);
        internal void ValidateWriter() => RequireHeader(Authority);
        internal void ExitWriter(bool publish) => _writer.Exit(publish);
        internal void BeginOpenRecovery() => _writer.BeginOpenRecovery();
        internal void Opened(int version) => _writer.Opened(version);
        internal void Replaced(int version) => _writer.Replaced(version);
        internal void PublishDurableCommit(int version) => _writer.Committed(version);
        public void ValidateWrite() => _writer.ValidateWrite();
        public void FenceUncertain() => _writer.FenceUncertain();
        public void StructuralBegin() => _writer.StructuralBegin();
        public void StructuralEnd(int version) => _writer.StructuralEnd(version);
        public void SlotReused() => _writer.SlotReused();
        public void Committed(int version) => _writer.Committed(version);

#if DEBUG || TESTING
        [ThreadStatic] internal static Action<string> PublicationStage;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal static void PublishStage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("PublicationStage");
            PublicationStage?.Invoke(stage);
#endif
        }

        private void Unregister(int version)
        {
            lock (_gate)
            {
                var generation = _versions[version];
                if (--generation.References == 0)
                {
                    Authority.Map.Store(Authority.Block + SharedMemoryLayout.ReaderOffset + generation.Slot * 8L, 0);
                    _versions.Remove(version);
                    _free.Push(generation.Slot);
                }
            }
            // Release can take ReadCache.Gate on final close. Never carry the
            // reentrant table gate into it: admission takes cache -> table.
            this.Release();
        }

        internal int[] LiveVersions()
        {
            lock (_gate)
            {
                if (_references == 0) return null;
                return _writer.Scan();
            }
        }

        internal static void RequireHeader(SharedMemoryAuthority authority, bool initializing = false)
        {
            if ((!initializing && authority.Map.Load(SharedMemoryLayout.Control) != 1) ||
                !authority.Map.Matches(0, authority.Header.Bytes) ||
                !authority.Map.Matches(SharedMemoryLayout.Page, authority.Header.Bytes))
                throw new IOException("Shared mapped authority changed or is damaged.");
        }

        // Caller holds main ownership. Unknown liveness/records means NO checkpoint
        // or reclamation; not a synthetic minimum which could lose exact floors.
        internal static int[] Scan(SharedMemoryAuthority authority, bool allowWriterFence = false)
        {
            try
            {
                RequireHeader(authority);
                // A writer may have died between any two mapped stores.  No
                // checkpoint or reclamation may treat that image as committed.
                if (!allowWriterFence && ((authority.Map.Load(SharedMemoryLayout.Control + 8) & 1) != 0 ||
                    authority.Map.Load(SharedMemoryLayout.RecoveryOwner) != 0)) return null;
                var versions = new HashSet<int>();
                for (var process = 0; process < authority.Header.Processes; process++)
                {
                    if (!authority.Locks.IsLive(process)) continue;
                    var block = SharedMemoryLayout.Blocks + (long)process * authority.Header.Stride;
                    if (authority.Map.Load(block) != 1 ||
                        SharedMemoryEnvelope.IsZero(authority.Map.Read(block + 16, 16)) ||
                        !authority.Map.Read(block + 32, 16).SequenceEqual(authority.Header.Authority.ToByteArray())) return null;
                    for (var generation = 0; generation < authority.Header.Generations; generation++)
                    {
                        var packed = authority.Map.Load(block + SharedMemoryLayout.ReaderOffset + generation * 8L);
                        if (packed == 0) continue;
                        var version = unchecked((int)packed);
                        if (version < 0 || unchecked((int)(packed >> 32)) != ~version) return null;
                        versions.Add(version);
                    }
                }
                return versions.ToArray();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ObjectDisposedException)
            { return null; }
        }

        private sealed class Generation { internal int Slot, References; }
        private sealed class Registration : IDisposable
        {
            private SharedMemoryReaderTable _table;
            private readonly int _version;
            internal Registration(SharedMemoryReaderTable table, int version) { _table = table; _version = version; }
            ~Registration() { this.Dispose(); }
            public void Dispose()
            {
                GC.SuppressFinalize(this);
                Interlocked.Exchange(ref _table, null)?.Unregister(_version);
            }
        }
    }
}
#endif
