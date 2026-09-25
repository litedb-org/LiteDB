#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// The coordinator's status, published in one memory-mapped page so that clients can
    /// decide about their snapshots without a round trip. One writer (the coordinator)
    /// updates it under a seqlock: the sequence is odd while fields change, and a reader
    /// accepts a copy only if it saw the same even sequence before and after.
    /// The file is named after the database path, so every coordinator of a database
    /// writes the same page. On Windows it lives in the per-user temp directory. On Unix
    /// it lives in a private 0700 subdirectory of a base that no other account can write
    /// to (see <see cref="TrustedBase"/>). A mode check alone cannot make a shared /tmp safe:
    /// a directory another account planted there stays under that account's control, which
    /// can change its mode after the check. Without such a base there is no page, and
    /// snapshots fall back to grants over IPC.
    /// </summary>
    internal sealed unsafe class CoordinatorStatusPage : IDisposable
    {
        internal const int Size = 4096;
        private const long Magic = 0x3130475044424C; // "LDBPG01"

        private const int SequenceOffset = 0;
        private const int MagicOffset = 8;
        private const int InstanceOffset = 16;
        private const int VersionOffset = 24;
        private const int StructuralOffset = 32;
        private const int ReuseEpochOffset = 40;
        private const int ResetsOffset = 48;

        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _base;
        private int _disposed;

        private CoordinatorStatusPage(FileStream file, bool writable)
        {
            var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            _map = MemoryMappedFile.CreateFromFile(file, null, Size, access, HandleInheritability.None, leaveOpen: false);
            try
            {
                _view = _map.CreateViewAccessor(0, Size, access);
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _base = pointer + _view.PointerOffset;
            }
            catch
            {
                _view?.Dispose();
                _map.Dispose();
                throw;
            }
        }

        private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>The page path; on Unix inside the caller's private directory (see the class remarks).</summary>
        internal static string PathFor(string filename) =>
            Path.Combine(PrivateDirectoryPath(), CoordinatorProtocol.PageName(filename));

        private static string PrivateDirectoryPath()
        {
            if (Windows) return Path.GetTempPath();
            // Candidates come from this process's own environment or the OS, never from a
            // path another account chose. A privileged process could enter a directory of
            // any owner, so it also only uses the root-owned run directories.
            var candidates = Environment.IsPrivilegedProcess
                ? new[] { Path.GetTempPath(), "/run", "/var/run" }
                : new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") };
            var root = TrustedBase(candidates);
            return root == null
                ? throw new UnauthorizedAccessException("No base directory that only its owner can write to holds the coordinator page.")
                : Path.Combine(root, "litedb-coord-" + Environment.UserName);
        }

        /// <summary>
        /// Unix: the first candidate that is an absolute directory without group or other
        /// write permission, so only its owner (this user, or root) can create, replace or
        /// remove the private directory in it. The shared /tmp (1777) never qualifies.
        /// Returns null when no candidate does.
        /// </summary>
        internal static string TrustedBase(string[] candidates)
        {
            const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate) || !Path.IsPathRooted(candidate)) continue;
                try
                {
                    if (Directory.Exists(candidate) && (File.GetUnixFileMode(candidate) & othersWrite) == 0)
                        return candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>
        /// Unix: create (when asked) and validate a private directory. It must be a real
        /// directory, not a symlink, with exactly mode 0700; anything else is rejected with
        /// UnauthorizedAccessException, which callers treat as "no status page".
        /// </summary>
        internal static bool EnsurePrivateDirectory(string directory, bool create)
        {
            if (Windows) return true;
            if (create && !Directory.Exists(directory) && !File.Exists(directory))
                Directory.CreateDirectory(directory, PrivateDirectoryMode);
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget != null)
                throw new UnauthorizedAccessException($"Coordinator directory '{directory}' is a symbolic link.");
            if (!info.Exists) return false;
            var mode = File.GetUnixFileMode(directory);
            if (mode != PrivateDirectoryMode)
                throw new UnauthorizedAccessException($"Coordinator directory '{directory}' has mode {mode}, not owner-only.");
            return true;
        }

        private static void RejectLink(string path)
        {
            if (!Windows && new FileInfo(path).LinkTarget != null)
                throw new UnauthorizedAccessException($"Coordinator status page '{path}' is a symbolic link.");
        }

        /// <summary>The coordinator's writable page, created with owner-only permissions.</summary>
        internal static CoordinatorStatusPage Create(string filename)
        {
            EnsurePrivateDirectory(PrivateDirectoryPath(), create: true);
            var path = PathFor(filename);
            RejectLink(path);
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete
            };
            if (!Windows) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var file = new FileStream(path, options);
            try
            {
                if (file.Length < Size) file.SetLength(Size);
                var page = new CoordinatorStatusPage(file, writable: true);
                page.Store(MagicOffset, Magic);
                return page;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>A client's read-only view, or null when no coordinator published one.</summary>
        internal static CoordinatorStatusPage TryOpen(string filename)
        {
            FileStream file = null;
            try
            {
                if (!EnsurePrivateDirectory(PrivateDirectoryPath(), create: false)) return null;
                var path = PathFor(filename);
                RejectLink(path);
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length < Size)
                {
                    file.Dispose();
                    return null;
                }
                return new CoordinatorStatusPage(file, writable: false);
            }
            catch (IOException) { file?.Dispose(); return null; }
            catch (UnauthorizedAccessException) { file?.Dispose(); return null; }
        }

        /// <summary>A consistent copy, or false after repeated concurrent updates or a torn page.</summary>
        internal bool TryRead(out CoordinatorStatus status)
        {
            status = default;
            // Callers serialize reads with Dispose; this only guards a misuse.
            if (Volatile.Read(ref _disposed) != 0) return false;
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var before = this.Load(SequenceOffset);
                if ((before & 1) != 0 || this.Load(MagicOffset) != Magic)
                {
                    Thread.SpinWait(8);
                    continue;
                }
                status = new CoordinatorStatus(this.Load(InstanceOffset), this.Load(VersionOffset),
                    this.Load(StructuralOffset), this.Load(ReuseEpochOffset), this.Load(ResetsOffset));
                Interlocked.MemoryBarrier();
                if (this.Load(SequenceOffset) == before) return true;
            }
            status = default;
            return false;
        }

        /// <summary>Single writer: the caller serializes calls.</summary>
        internal void Write(in CoordinatorStatus status)
        {
            var sequence = this.Load(SequenceOffset);
            // A crashed predecessor may have left the sequence odd.
            if ((sequence & 1) != 0) sequence++;
            this.Store(SequenceOffset, sequence + 1);
            Interlocked.MemoryBarrier();
            this.Store(InstanceOffset, status.Instance);
            this.Store(VersionOffset, status.Version);
            this.Store(StructuralOffset, status.Structural);
            this.Store(ReuseEpochOffset, status.ReuseEpoch);
            this.Store(ResetsOffset, status.Resets);
            Interlocked.MemoryBarrier();
            this.Store(SequenceOffset, sequence + 2);
        }

        /// <summary>Test hook: use the 32-bit read path on 64-bit processes too.</summary>
        internal static bool ForceSplitLoads;

        private long Load(int offset)
        {
            var location = _base + offset;
            if (Environment.Is64BitProcess && !ForceSplitLoads) return Volatile.Read(ref *(long*)location);
            return LoadSplit((int*)location);
        }

        /// <summary>
        /// A 64-bit read without writing. On 32-bit runtimes Volatile.Read(ref long) is
        /// Interlocked.CompareExchange(ref x, 0, 0), a write access that faults on a client's
        /// read-only view and terminates the process. The high word is re-read until stable,
        /// so the result is a value the single writer actually stored (its 64-bit writes are
        /// atomic); the seqlock in <see cref="TryRead"/> still detects torn multi-field copies.
        /// </summary>
        internal static long LoadSplit(IntPtr address) => LoadSplit((int*)address);

        private static long LoadSplit(int* location)
        {
            while (true)
            {
                var high = Volatile.Read(ref location[1]);
                var low = Volatile.Read(ref location[0]);
                if (Volatile.Read(ref location[1]) == high) return ((long)high << 32) | (uint)low;
            }
        }

        private void Store(int offset, long value) => Volatile.Write(ref *(long*)(_base + offset), value);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _map.Dispose();
        }
    }

    /// <summary>
    /// One coordinator's published state. <see cref="Structural"/> is odd while a
    /// checkpoint, promotion, header rewrite or startup recovery may change bytes a
    /// snapshot open reads; <see cref="Resets"/> counts read-version decreases (WAL
    /// truncation); <see cref="Instance"/> is zero when no coordinator is running.
    /// </summary>
    internal readonly struct CoordinatorStatus
    {
        internal CoordinatorStatus(long instance, long version, long structural, long reuseEpoch, long resets)
        {
            this.Instance = instance;
            this.Version = version;
            this.Structural = structural;
            this.ReuseEpoch = reuseEpoch;
            this.Resets = resets;
        }

        internal long Instance { get; }
        internal long Version { get; }
        internal long Structural { get; }
        internal long ReuseEpoch { get; }
        internal long Resets { get; }
        internal bool Quiet => this.Instance != 0 && (this.Structural & 1) == 0;
    }
}
#endif
