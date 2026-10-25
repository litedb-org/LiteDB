#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Lifetime references are facade/result edges, never per call or per row.
    // Equal versions share one record. All distinct versions must remain visible.
    internal sealed class SharedMemoryReaderTable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<int, Generation> _versions = new Dictionary<int, Generation>();
        private readonly Stack<int> _free = new Stack<int>();
        internal readonly SharedMemoryAuthority Authority;
        private readonly NativeAdmission _admission;
        private int _references = 1;
        internal bool Closed { get { lock (_gate) return _references == 0; } }
        internal bool HasReaders { get { lock (_gate) return _versions.Count != 0; } }

        private SharedMemoryReaderTable(SharedMemoryAuthority authority, NativeAdmission admission)
        {
            Authority = authority;
            _admission = admission;
            for (var i = authority.Header.Generations - 1; i >= 0; i--) _free.Push(i);
        }

        internal static SharedMemoryReaderTable Open(EngineSettings settings)
        {
            if (settings.Upgrade || settings.AutoRebuild)
                throw new NotSupportedException("ABI-2 file-replacing upgrade/auto-rebuild requires slice 9 qualification.");
            var captured = settings.Clone();
            captured.SharedMode = true;
            var admission = NativeAdmission.Open(captured, true);
            try
            {
                // Offline migration is deliberately not automatic in slices 0–4.
                // Never delete old/unknown files; the operator must stop old clients.
                if (Directory.Exists(captured.Filename + "-readers") ||
                    new[] { "-shared-live", "-shared-state", "-shared-disabled" }.Any(s => File.Exists(captured.Filename + s)))
                    throw new IOException("Stop all ABI-1 participants and remove recognized legacy coordination offline before enabling SharedOneFile.");
                var authority = SharedMemoryAuthority.Open(captured, admission.Identity, current =>
                {
                    using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(captured.Filename, captured.SharedMutexNameStrategy));
                    var held = false;
                    try
                    {
                        try { held = main.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
                        if (!held) throw new IOException("Protected Shared storage validation timed out.");
                        var coreSettings = captured.Clone();
                        coreSettings.SharedAdmission = admission;
                        coreSettings.SharedReaderVersions = () => Scan(current);
                        coreSettings.AutoRebuildAllowed = () => false;
                        using var core = new LiteEngine(coreSettings);
                    }
                    finally { if (held) main.ReleaseMutex(); }
                });
                return new SharedMemoryReaderTable(authority, admission);
            }
            catch { admission.Dispose(); throw; }
        }

        internal void Retain()
        {
            lock (_gate)
            {
                if (_references == 0) throw new ObjectDisposedException(nameof(SharedMemoryReaderTable));
                _references++;
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                if (--_references != 0) return;
                try { Authority.Dispose(); }
                finally { _admission.Dispose(); }
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
                this.Release();
            }
        }

        internal int[] LiveVersions()
        {
            lock (_gate) return _references == 0 ? null : Scan(Authority);
        }

        private static void RequireHeader(SharedMemoryAuthority authority)
        {
            if (authority.Map.Load(SharedMemoryLayout.Control) != 1 ||
                !authority.Map.Read(0, 256).SequenceEqual(authority.Header.Bytes) ||
                !authority.Map.Read(SharedMemoryLayout.Page, 256).SequenceEqual(authority.Header.Bytes))
                throw new IOException("Shared mapped authority changed or is damaged.");
        }

        // Caller holds main ownership. Unknown liveness/records means NO checkpoint
        // or reclamation; not a synthetic minimum which could lose exact floors.
        internal static int[] Scan(SharedMemoryAuthority authority)
        {
            try
            {
                RequireHeader(authority);
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
