#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Bootstrap mutex is independent of writer tenure. A live peer can claim its
    // slot and publish a request before it waits for the main writer mutex.
    internal sealed class SharedMemoryAuthority : IDisposable
    {
        internal readonly SharedRangeLocks Locks;
        internal SharedMemoryMap Map { get; private set; }
        internal SharedMemoryEnvelope Header { get; private set; }
        internal int Slot { get; private set; } = -1;
        internal SharedWriterRequests Requests { get; private set; }
        internal readonly Guid Incarnation = Guid.NewGuid();
        internal long Block => SharedMemoryLayout.Blocks + (long)Slot * Header.Stride;
        private FileStream _file;
        private bool _disposed;
        internal static string PathFor(string path) => path + "-shm";
#if DEBUG || TESTING
        [ThreadStatic] internal static Action<string> BootstrapStage;
        [ThreadStatic] internal static Func<FileStream, long, int> AllocateFile;
        [ThreadStatic] internal static Func<FileStream, long, SharedMemoryMap> MapFile;
        internal static int BootstrapReuseCount;
        internal static int BootstrapResetCount;
#endif

        private SharedMemoryAuthority(string path) { Locks = new SharedRangeLocks(path); }

        internal static SharedMemoryAuthority Open(EngineSettings settings, string identity, Action validateStorage) =>
            Open(settings, identity, _ => validateStorage());

        internal static SharedMemoryAuthority Open(EngineSettings settings, string identity, Action<SharedMemoryAuthority> validateStorage)
        {
            RebuildRecovery.EnsureAvailable(settings);
            RequirePlatform(settings.Filename);
            var path = PathFor(settings.Filename);
            using var bootstrap = SharedMutexFactory.Create(SharedMutexNameFactory.CreateUsingSha1(path) + ".Bootstrap");
            var held = false;
            SharedMemoryAuthority result = null;
            try
            {
                try { held = bootstrap.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Shared bootstrap is busy.");
                RebuildRecovery.EnsureAvailable(settings);
                result = new SharedMemoryAuthority(path);
                Stage("created");
                result._file = new FileStream(DatabaseFileIdentity.Open(path, false, false, writable: true), FileAccess.ReadWrite);
                if (DatabaseFileIdentity.Read(result._file.SafeFileHandle) != result.Locks.Identity)
                    throw new IOException("Shared mapped file identity differs from liveness owner.");
                using (var verify = new DatabaseFileLock(path, false, false))
                    if (verify.Identity != result.Locks.Identity) throw new IOException("Shared pathname replaced during open.");
                var first = result.Locks.TryExclusiveDms();
                if (first) result.Locks.EnterWrite();
                else result.Locks.ShareDms();
                var old = result.ReadExisting(settings.Filename, identity, first, settings.SharedMutexNameStrategy);
                var requested = SharedMemoryEnvelope.Create(settings, identity);
                var reuse = old != null && first && result.CanReuse(old, requested, identity, settings.Filename,
                    settings.SharedMutexNameStrategy);
                if (first && !reuse)
                {
                    if (old != null) SharedWakeSignal.Retire(old.Authority);
#if DEBUG || TESTING
                    Interlocked.Increment(ref BootstrapResetCount);
#endif
                    result.Header = SharedMemoryEnvelope.Create(settings, identity);
                    // Invalidate both old envelopes before changing length. A killed
                    // resizer must not leave old valid length declarations behind.
                    if (result._file.Length != 0)
                        using (var oldMap = new SharedMemoryMap(result._file, result._file.Length))
                            oldMap.Zero(0, Math.Min(oldMap.Size, 2 * SharedMemoryLayout.Page));
                    Stage("invalidated");
                    result._file.SetLength(result.Header.Size);
                    Allocate(result._file, result.Header.Size);
                    Stage("allocated");
                    result.Map = CreateMap(result._file, result.Header.Size, result.Header.Authority);
                    Stage("mapped");
                    result.Map.Zero(0, result.Header.Size);
                    result.Map.Store(SharedMemoryLayout.CommittedVersion, -1);
                    result.Map.Store(SharedMemoryLayout.FastAdmissionDisabled, 1);
                    result.Map.Store(SharedMemoryLayout.Control + 8, 1); // odd, not Ready
                    Stage("odd");
                    result.Map.Write(0, result.Header.Bytes);
                    Stage("envelope-a");
                    result.Map.Write(SharedMemoryLayout.Page, result.Header.Bytes);
                    Stage("envelope-b");
                    result.Map.PrepareWake();
                    Stage("before-storage");
                    validateStorage(result); // must open/recover independently under main
                    Stage("after-storage");
                    result.Map.Store(SharedMemoryLayout.Control + 8, 2);
                    Stage("before-ready");
                    result.Map.Store(SharedMemoryLayout.Control, 1);
                    Stage("ready");
                    Stage("before-dms-downgrade");
                    result.Locks.DowngradeDms();
                    Stage("downgraded");
                    result.Locks.ExitWrite();
                    Stage("write-released");
                }
                else
                {
                    result.Header = old ?? throw new IOException("Live Shared authority has no valid envelopes.");
                    result.Map = CreateMap(result._file, result.Header.Size, result.Header.Authority);
                    // A writer can die after making the publication sequence odd.
                    // The mapping remains attachable so a successor can take the
                    // named mutex + WRITE tenure, validate DB/WAL, and repair it.
                    if (!reuse && (result.Map.Load(SharedMemoryLayout.Control) != 1 ||
                        result.Map.Load(SharedMemoryLayout.Control + 8) == 0))
                        throw new IOException("Live Shared authority is not Ready.");
                    result.Map.PrepareWake();
                }
                if (first && reuse)
                {
#if DEBUG || TESTING
                    Interlocked.Increment(ref BootstrapReuseCount);
#endif
                    // Keep a deterministic interruption point before reuse validation;
                    // it performs no envelope write and leaves the authority retryable.
                    Stage("envelope-b");
                    Stage("before-storage");
                    validateStorage(result);
                    Stage("after-storage");
                    result.Map.Store(SharedMemoryLayout.Control + 8, 2);
                    Stage("before-ready");
                    result.Map.Store(SharedMemoryLayout.Control, 1);
                    Stage("ready");
                    Stage("before-dms-downgrade");
                    result.Locks.DowngradeDms();
                    Stage("downgraded");
                    result.Locks.ExitWrite();
                    Stage("write-released");
                }
                result.Claim();
                Stage("claimed");
                result.Requests = new SharedWriterRequests(result);
                bootstrap.ReleaseMutex();
                held = false;
                if (!first)
                {
                    using (result.Requests.Enter())
                    { Stage("before-storage"); validateStorage(result); Stage("after-storage"); }
                }
                return result;
            }
            catch { result?.Dispose(); throw; }
            finally { if (held) bootstrap.ReleaseMutex(); }
        }

        private SharedMemoryEnvelope ReadExisting(string path, string identity, bool first, SharedMutexNameStrategy strategy)
        {
            var length = _file.Length;
            if (length == 0) return null;
            if (length > SharedMemoryEnvelope.MaximumSize) throw new IOException("Unsupported Shared map length.");
            using var initial = new SharedMemoryMap(_file, length);
            var count = (int)Math.Min(length, 2 * SharedMemoryLayout.Page);
            var bytes = initial.Read(0, count);
            if (SharedMemoryEnvelope.IsZero(bytes))
            {
                if (!first) throw new IOException("Live Shared authority is empty.");
                return null;
            }
            SharedMemoryEnvelope valid = null;
            var missing = false;
            foreach (var offset in new[] { 0, SharedMemoryLayout.Page })
            {
                if (bytes.Length < offset + SharedMemoryLayout.EnvelopeBytes)
                    throw new IOException("Partial Shared envelope: stop participants and remove the damaged ephemeral authority.");
                var candidate = bytes.Skip(offset).Take(SharedMemoryLayout.EnvelopeBytes).ToArray();
                if (SharedMemoryEnvelope.IsZero(candidate))
                {
                    if (!first) throw new IOException("Live Shared envelope is incomplete.");
                    missing = true;
                    continue;
                }
                var decoded = SharedMemoryEnvelope.DecodeForReuse(candidate, path, quiescent: first, strategy: strategy);
                if (!decoded.MatchesIdentity(identity) && !first)
                    throw new IOException("Shared coordination database identity/namespace mismatch.");
                if (valid != null && !valid.Bytes.SequenceEqual(decoded.Bytes)) throw new IOException("Shared envelopes disagree.");
                valid = decoded;
            }
            if (missing) return null;
            if (valid != null && length != valid.Size) throw new IOException("Shared map length disagrees with envelope.");
            return valid;
        }

        private bool CanReuse(SharedMemoryEnvelope existing, SharedMemoryEnvelope requested, string identity,
            string path, SharedMutexNameStrategy strategy)
        {
            if (!existing.HasValidChecksum() || !existing.MatchesIdentity(identity) || !existing.MatchesNamespace(path, strategy)) return false;
            if (existing.Size != requested.Size || existing.Processes != requested.Processes ||
                existing.Generations != requested.Generations || existing.Stride != requested.Stride)
                return false;
            return true;
        }

        internal void Rekey(EngineSettings settings, string identity)
        {
            // Sole DMS + bootstrap + main + WRITE remain held throughout. Preserve
            // dimensions and counters; invalidating both envelopes makes death at
            // either envelope store an offline reset, never a live mixed authority.
            var dimensions = settings.Clone();
            dimensions.SharedMemorySize = Header.Size;
            dimensions.SharedProcessCapacity = Header.Processes;
            dimensions.SharedGenerationCapacity = Header.Generations;
            var next = SharedMemoryEnvelope.Create(dimensions, identity);
            var previous = Header.Authority;
            // Existing native handles remain usable after unlink. Retire while
            // the old identity is still recoverable, before either envelope goes.
            SharedWakeSignal.Retire(previous);
            Map.Zero(0, 2 * SharedMemoryLayout.Page);
            SharedMemoryMaintenance.Stage("rekey-invalidated");
            Header = next;
            Map.Write(0, Header.Bytes);
            SharedMemoryMaintenance.Stage("rekey-envelope-a");
            Map.Write(SharedMemoryLayout.Page, Header.Bytes);
            SharedMemoryMaintenance.Stage("rekey-envelope-b");
            Map.RekeyWake(previous, Header.Authority);
            if (Slot >= 0) Map.Write(Block + 32, Header.Authority.ToByteArray());
        }

        private void Claim()
        {
            for (var slot = 0; slot < Header.Processes; slot++)
            {
                if (!Locks.TryClaim(slot)) continue;
                Slot = slot;
                Map.Zero(Block, Header.Stride);
                Map.Write(Block + 16, Incarnation.ToByteArray());
                Map.Write(Block + 32, Header.Authority.ToByteArray());
                Map.Store(Block + 48, Environment.ProcessId);
                Map.Store(Block, 1); // Ready last
                return;
            }
            throw new IOException("Shared process capacity exhausted before storage execution.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Stage("before-close");
                if (Map != null && Slot >= 0) Map.Store(Block, 0);
                SharedMemoryFinalClose.Stage("slot-cleared");
            }
            finally
            {
                try { Map?.Dispose(); }
                finally { try { _file?.Dispose(); } finally { Locks.Dispose(); } }
            }
            SharedMemoryFinalClose.Stage("dms-released");
            Stage("closed");
        }

        // Linux x64 resumes after the process-death matrices were split from the
        // shared session. Linux ARM64 uses the same mapped ABI and native futex
        // path; the other backends remain refused until their own qualification.
        internal static bool IsPlatformQualified => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
             RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

        private static void RequirePlatform(string path)
        {
            if (!IsPlatformQualified)
                throw new PlatformNotSupportedException("ABI-2 Shared is disabled pending complete hosted platform qualification.");
            var failure = SharedCoordinationPolicy.VolumeFailure(path);
            if (failure != null) throw new IOException("ABI-2 requires qualified coherent local storage: " + failure);
        }
        private static void Allocate(FileStream file, long length)
        {
            int error;
#if DEBUG || TESTING
            if (AllocateFile != null)
            {
                Reachability.FaultPoint("AllocateFile");
                error = AllocateFile(file, length);
            }
            else error = SharedPlatformAllocation.Allocate(file, length);
#else
            error = SharedPlatformAllocation.Allocate(file, length);
#endif
            if (error != 0) throw new IOException("Shared allocation failed (native error " + error + ").");
        }
        private static SharedMemoryMap CreateMap(FileStream file, long size, Guid authority)
        {
#if DEBUG || TESTING
            if (MapFile != null)
            {
                Reachability.FaultPoint("MapFile");
                return MapFile(file, size);
            }
#endif
            return new SharedMemoryMap(file, size, authority);
        }
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        private static void Stage(string stage)
        {
#if DEBUG || TESTING
            if (BootstrapStage != null)
            {
                Reachability.FaultPoint("BootstrapStage");
                BootstrapStage(stage);
            }
#endif
        }
    }
}
#endif
