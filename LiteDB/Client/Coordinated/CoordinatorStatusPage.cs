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
    /// The file lives in the per-user temp directory under a name derived from the
    /// database path, so every coordinator of a database writes the same page.
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

        internal static string PathFor(string filename) =>
            Path.Combine(Path.GetTempPath(), CoordinatorProtocol.PipeName(filename) + ".page");

        /// <summary>The coordinator's writable page, created with owner-only permissions.</summary>
        internal static CoordinatorStatusPage Create(string filename)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete
            };
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var file = new FileStream(PathFor(filename), options);
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
                file = new FileStream(PathFor(filename), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

        private long Load(int offset) => Volatile.Read(ref *(long*)(_base + offset));

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
