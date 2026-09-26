#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Experimental same-version coordination. Attach/retire and all publications require
    /// the database mutex. Readers serialize local access with disposal. No stored page is
    /// trusted until an engine has opened under that mutex in this process.
    /// </summary>
    internal sealed unsafe class SharedCoordinationPage : ICoordinationSignals, IDisposable
    {
        private const int Size = 4096;
        private const long Magic = SharedCoordinationFallback.Magic;
        private readonly string _filename;
        private readonly FileStream _participation;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly long* _fields;
        private readonly object _writeLock = new object();
        private int _depth;
        private bool _trusted;
        private bool _disposed;

        private SharedCoordinationPage(string filename, FileStream participation, FileStream file)
        {
            _filename = filename;
            _participation = participation;
            _map = MemoryMappedFile.CreateFromFile(file, null, Size, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: false);
            var acquired = false;
            try
            {
                _view = _map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                byte* address = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
                acquired = true;
                _fields = (long*)(address + _view.PointerOffset);
                if (((long)_fields & 7) != 0) throw new IOException("Unaligned Shared status page.");
            }
            catch
            {
                if (acquired) _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view?.Dispose();
                _map.Dispose();
                throw;
            }
        }

        internal static string PagePath(string filename) => SharedCoordinationFallback.PagePath(filename);
        internal static string DisabledPath(string filename) => SharedCoordinationFallback.DisabledPath(filename);
        private static string LivePath(string filename) => SharedCoordinationFallback.LivePath(filename);

        /// <summary>Caller owns the database mutex. Failure requires revocation before writable fallback.</summary>
        internal static SharedCoordinationPage Open(string filename)
        {
            FileStream participation = null;
            FileStream file = null;
            try
            {
                // The participation file is never written after creation. Shared read
                // handles prove liveness without a PID, timestamp, or heartbeat.
                if (!File.Exists(LivePath(filename)))
                    using (var live = new FileStream(LivePath(filename), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = BitConverter.GetBytes(Magic);
                        live.Write(bytes, 0, bytes.Length);
                    }
                participation = new FileStream(LivePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!SharedCoordinationFallback.IsOwned(participation)) throw new IOException("Unknown Shared participation file.");
                var created = !File.Exists(PagePath(filename));
                file = new FileStream(PagePath(filename), created ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                if (created) file.SetLength(Size);
                if (file.Length != Size) throw new IOException("Unknown Shared status page size.");
                var page = new SharedCoordinationPage(filename, participation, file);
                if (created)
                {
                    page.Store(6, NewIdentity());
                    page.Store(2, -1);
                    page.Store(0, Magic);
                }
                if (page.Load(0) != Magic)
                {
                    page.Dispose();
                    throw new IOException("Unknown Shared status protocol.");
                }
                return page;
            }
            catch
            {
                file?.Dispose();
                participation?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// A failed mapping must revoke every existing fast reader before an unannounced
        /// writer runs. Failure to publish revocation propagates before database mutation.
        /// </summary>
        internal static void Revoke(string filename) => SharedCoordinationFallback.Revoke(filename);

        private bool Revoked()
        {
            try { File.GetAttributes(DisabledPath(_filename)); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal bool TryRead(out SharedCoordinationStatus status)
        {
            lock (_writeLock) return this.TryReadCore(out status);
        }

        private bool TryReadCore(out SharedCoordinationStatus status)
        {
            status = default;
            if (_disposed || !_trusted || this.Revoked()) return false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var sequence = this.Load(1);
                if ((sequence & 1) != 0 || this.Load(0) != Magic) continue;
                status = new SharedCoordinationStatus(this.Load(2), this.Load(3), this.Load(4), this.Load(5), this.Load(6));
                Interlocked.MemoryBarrier();
                if (sequence == this.Load(1)) return status.Quiet && !this.Revoked();
            }
            status = default;
            return false;
        }

        /// <summary>Publish the result of an independently validated open under writer ownership.</summary>
        internal void Opened(int version)
        {
            this.Committed(version);
            _trusted = true;
        }

        public void StructuralBegin()
        {
            lock (_writeLock)
            {
                if (_depth++ != 0) return;
                this.Change(() => this.Store(3, this.Load(3) | 1));
            }
        }

        public void StructuralEnd(int version)
        {
            lock (_writeLock)
            {
                if (_depth <= 0) throw new InvalidOperationException("Unbalanced Shared structural publication.");
                if (--_depth != 0) return;
                this.Change(() =>
                {
                    if (version >= 0) this.SetVersion(version);
                    this.Store(3, checked(this.Load(3) + 1));
                });
            }
        }

        public void SlotReused()
        {
            lock (_writeLock) this.Change(() => this.Store(4, checked(this.Load(4) + 1)));
        }

        public void Committed(int version)
        {
            lock (_writeLock) this.Change(() => this.SetVersion(version));
        }

        private void SetVersion(int version)
        {
            if (version < this.Load(2)) this.Store(5, checked(this.Load(5) + 1));
            this.Store(2, version);
        }

        private void Change(Action change)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SharedCoordinationPage));
            var before = this.Load(1);
            // An interrupted publisher leaves an odd sequence. The caller has recovered
            // ownership; every older snapshot fence must be invalidated before reuse.
            if ((before & 1) != 0)
            {
                this.Store(6, NewIdentity());
                before = checked(before + 1);
            }
            this.Store(1, checked(before + 1));
            change();
            this.Store(1, checked(before + 2));
        }

        // Read/write views on every runtime: Interlocked's full fences and aligned 64-bit
        // atomics are required. No MemoryMappedViewAccessor.Write participates in ordering.
        private long Load(int field) => Interlocked.CompareExchange(ref _fields[field], 0, 0);
        private void Store(int field, long value) => Interlocked.Exchange(ref _fields[field], value);
        private static long NewIdentity() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) | 1;

        public void Dispose()
        {
            lock (_writeLock) this.Close();
        }

        private void Close()
        {
            if (_disposed) return;
            _disposed = true;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _map.Dispose();
            _participation.Dispose();
        }

        /// <summary>Called under the database mutex, only after disposing this participant.</summary>
        internal static void TryRetire(string filename) => SharedCoordinationFallback.TryRetire(filename);
    }

    internal readonly struct SharedCoordinationStatus
    {
        internal SharedCoordinationStatus(long version, long structural, long reuse, long resets, long identity)
        { Version = version; Structural = structural; Reuse = reuse; Resets = resets; Identity = identity; }
        internal long Version { get; }
        internal long Structural { get; }
        internal long Reuse { get; }
        internal long Resets { get; }
        internal long Identity { get; }
        internal bool Quiet => Version >= 0 && Identity != 0 && (Structural & 1) == 0;
        internal bool SameStorage(SharedCoordinationStatus other) => Identity == other.Identity &&
            Structural == other.Structural && Reuse == other.Reuse && Resets == other.Resets;
    }
}
#endif
