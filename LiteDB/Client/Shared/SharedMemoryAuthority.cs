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
                    result.Map = CreateMap(result._file, result.Header.Size);
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
                    result.Map = CreateMap(result._file, result.Header.Size);
                    // A writer can die after making the publication sequence odd.
                    // The mapping remains attachable so a successor can take the
                    // named mutex + WRITE tenure, validate DB/WAL, and repair it.
                    if (!reuse && (result.Map.Load(SharedMemoryLayout.Control) != 1 ||
                        result.Map.Load(SharedMemoryLayout.Control + 8) == 0))
                        throw new IOException("Live Shared authority is not Ready.");
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
                // A future lingering writer sees the peer/request before this open
                // can block on its main mutex. The wake word is reserved, not active.
                result.Map.Store(result.Block + SharedMemoryLayout.RequestOffset, 1);
                bootstrap.ReleaseMutex();
                held = false;
                if (!first) { Stage("before-storage"); validateStorage(result); Stage("after-storage"); }
                result.Map.Store(result.Block + SharedMemoryLayout.RequestOffset, 0);
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
            }
            finally
            {
                try { Map?.Dispose(); }
                finally { try { _file?.Dispose(); } finally { Locks.Dispose(); } }
            }
            Stage("closed");
        }

        internal static bool IsPlatformQualified => BitConverter.IsLittleEndian &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64;

        private static void RequirePlatform(string path)
        {
            // Initial qualification is Linux x64 only. Backends for other systems
            // compile, but do not silently claim mapped-atomic/platform qualification.
            if (!IsPlatformQualified)
                throw new PlatformNotSupportedException("ABI-2 Shared prototype currently requires Linux x64 qualification.");
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
            else error = posix_fallocate(file.SafeFileHandle, 0, length);
#else
            error = posix_fallocate(file.SafeFileHandle, 0, length);
#endif
            if (error != 0) throw new IOException("Shared allocation failed (native error " + error + ").");
        }
        private static SharedMemoryMap CreateMap(FileStream file, long size)
        {
#if DEBUG || TESTING
            if (MapFile != null)
            {
                Reachability.FaultPoint("MapFile");
                return MapFile(file, size);
            }
#endif
            return new SharedMemoryMap(file, size);
        }
        [DllImport("libc", SetLastError = true)] private static extern int posix_fallocate(Microsoft.Win32.SafeHandles.SafeFileHandle file, long offset, long length);
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
