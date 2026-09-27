#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Versioned coordination ABI. Attach/retire and storage publications require
    /// the database mutex. Scheduling hints do not authorize storage access. Readers serialize local access with disposal. No stored page is
    /// trusted until an engine has opened under that mutex in this process.
    /// </summary>
    internal sealed unsafe class SharedCoordinationPage : IBatchedCoordinationSignals, IDisposable
    {
        private const int Size = SharedCoordinationProtocol.PageSize;
        private readonly string _revocationPath;
        private readonly FileStream _participation;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly long* _fields;
        private readonly long* _header;
        private readonly long[] _expectedHeader;
        private readonly object _writeLock = new object();
        private int _depth;
        private bool _trusted;
        private bool _disposed;
        private bool _pointerAcquired;

        private SharedCoordinationPage(string filename, FileStream participation, FileStream file, byte[] header)
        {
            _revocationPath = DisabledPath(filename);
            _participation = participation;
            _expectedHeader = new long[SharedCoordinationProtocol.HeaderSize / sizeof(long)];
            for (var i = 0; i < _expectedHeader.Length; i++)
                _expectedHeader[i] = SharedCoordinationProtocol.Read(header, i * sizeof(long));
            _map = MemoryMappedFile.CreateFromFile(file, null, Size, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: false);
            try
            {
                _view = _map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                byte* address = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
                _pointerAcquired = true;
                _header = (long*)(address + _view.PointerOffset);
                _fields = _header + _expectedHeader.Length - 1;
                if (((long)_fields & 7) != 0) throw new IOException("Unaligned Shared status page.");
            }
            catch
            {
                if (_pointerAcquired)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _pointerAcquired = false;
                }
                this.CloseView();
                _map.Dispose();
                throw;
            }
        }

        internal static string PagePath(string filename) => SharedCoordinationFallback.PagePath(filename);
        internal static string DisabledPath(string filename) => SharedCoordinationFallback.DisabledPath(filename);

        /// <summary>Caller owns the database mutex. Failure requires revocation before writable fallback.</summary>
        internal static SharedCoordinationPage Open(string filename)
        {
            FileStream participation = null;
            FileStream file = null;
            try
            {
                var header = SharedCoordinationFiles.Open(filename, out participation, out file);
                var page = new SharedCoordinationPage(filename, participation, file, header);
                if (!page.HeaderMatches())
                {
                    page.Dispose();
                    throw new IOException("Shared coordination header changed during attachment: " + filename);
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

        private bool Revoked() => SharedCoordinationRevocation.IsRevoked(_revocationPath);

        // A short scheduling hint, not a lock or a liveness proof. A killed requester
        // cannot strand readers: they merely yield, and the hint expires independently.
        internal long RequestWriterTurn(long now)
        {
            lock (_writeLock)
            {
                if (_disposed || !this.HeaderMatches()) return 0;
                // One atomic word holds a wrapping deadline, a request sequence and
                // the active bit. Completion retains the sequence, so requests made
                // in the same millisecond cannot clear each other's hints.
                while (true)
                {
                    var previous = this.Load(7);
                    var request = unchecked((long)(uint)(now + 100) << 32) |
                        (unchecked(previous + 2) & 0xfffffffeL) | 1;
                    if (Interlocked.CompareExchange(ref _fields[7], request, previous) == previous) return request;
                }
            }
        }

        internal void EndWriterTurn(long request)
        {
            lock (_writeLock)
                if (!_disposed && request != 0 && this.HeaderMatches())
                    Interlocked.CompareExchange(ref _fields[7], request & ~1L, request);
        }

        internal bool ShouldYieldToWriter(long now)
        {
            lock (_writeLock)
            {
                if (_disposed) return false;
                var request = this.Load(7);
                var remaining = unchecked((uint)(request >> 32) - (uint)now);
                return (request & 1) != 0 && remaining > 0 && remaining <= 100;
            }
        }

        internal bool TryRead(out SharedCoordinationStatus status)
        {
            lock (_writeLock) return this.TryReadCore(out status, checkRevocation: true);
        }

        // A hint cannot authorize a read: admission must publish its lease and then
        // call TryRead, including the filesystem revocation checks, before any query.
        internal bool TryReadHint(out SharedCoordinationStatus status)
        {
            lock (_writeLock) return this.TryReadCore(out status, checkRevocation: false);
        }

        private bool TryReadCore(out SharedCoordinationStatus status, bool checkRevocation)
        {
            status = default;
            if (_disposed || !_trusted || !this.HeaderMatches() || (checkRevocation && this.Revoked())) return false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var sequence = this.Load(1);
                if ((sequence & 1) != 0) continue;
                status = new SharedCoordinationStatus(this.Load(2), this.Load(3), this.Load(4), this.Load(5), this.Load(6));
                Interlocked.MemoryBarrier();
                if (sequence == this.Load(1)) return status.Quiet && this.HeaderMatches() && (!checkRevocation || !this.Revoked());
            }
            status = default;
            return false;
        }

        // Immutable ABI and authority words must still match the validated attachment.
        // No allocation, hashing or file I/O on the mapped admission path.
        private bool HeaderMatches()
        {
            for (var i = 0; i < _expectedHeader.Length; i++)
                if ((IntPtr.Size == 8 ? Volatile.Read(ref _header[i]) :
                    Interlocked.CompareExchange(ref _header[i], 0, 0)) != _expectedHeader[i]) return false;
            return true;
        }

        // Called under the database mutex. An interrupted publisher needs the old
        // broad recovery fence until an independent engine open has validated storage.
        internal bool BeginOpenRecovery()
        {
            lock (_writeLock)
            {
                this.EnsureWritable();
                if ((this.Load(1) & 1) == 0 && (this.Load(3) & 1) == 0) return false;
                this.StructuralBegin();
                return true;
            }
        }

        /// <summary>Publish the result of an independently validated open under writer ownership.</summary>
        internal void Opened(int version, bool endStructural = false)
        {
            lock (_writeLock)
            {
                if (endStructural) this.StructuralEnd(version);
                else this.Committed(version);
                _trusted = true;
            }
        }

        public void StructuralBegin()
        {
            lock (_writeLock)
            {
                if (_depth++ != 0) return;
                var before = this.BeginChange();
                this.Store(3, this.Load(3) | 1);
                this.EndChange(before);
            }
        }

        public void StructuralEnd(int version)
        {
            lock (_writeLock)
            {
                if (_depth <= 0) throw new InvalidOperationException("Unbalanced Shared structural publication.");
                if (--_depth != 0) return;
                var before = this.BeginChange();
                if (version >= 0) this.SetVersion(version);
                this.Store(3, checked(this.Load(3) + 1));
                this.EndChange(before);
            }
        }

        public void SlotReused()
        {
            lock (_writeLock)
            {
                var before = this.BeginChange();
                this.Store(4, checked(this.Load(4) + 1));
                this.EndChange(before);
            }
        }

        public void Committed(int version)
        {
            lock (_writeLock)
            {
                this.EnsureWritable();
                var current = this.Load(2);
                if (version >= current && (this.Load(1) & 1) == 0)
                {
                    // Append commits leave every earlier snapshot valid. One atomic
                    // publication suffices; destructive transitions retain sequencing.
                    if (version != current) this.Store(2, version);
                    return;
                }
                var before = this.BeginChange();
                this.SetVersion(version);
                this.EndChange(before);
            }
        }

        private void SetVersion(int version)
        {
            if (version < this.Load(2)) this.Store(5, checked(this.Load(5) + 1));
            this.Store(2, version);
        }

        private void EnsureWritable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SharedCoordinationPage));
            if (!this.HeaderMatches()) throw new IOException("Shared coordination header changed after attachment.");
        }

        private long BeginChange()
        {
            this.EnsureWritable();
            var before = this.Load(1);
            // An interrupted publisher leaves an odd sequence. The caller has recovered
            // ownership; every older snapshot fence must be invalidated before reuse.
            if ((before & 1) != 0)
            {
                this.Store(6, NewIdentity());
                before = checked(before + 1);
            }
            this.Store(1, checked(before + 1));
            return before;
        }

        // A failed field update deliberately leaves an odd sequence for recovery.
        private void EndChange(long before) => this.Store(1, checked(before + 2));

        // Aligned 64-bit acquire loads do not write the shared cache line on x64/ARM64.
        // Keep the conservative atomic read on x86. The seqlock validation fence and
        // the post-lease admission fence remain full barriers.
        private long Load(int field) => IntPtr.Size == 8
            ? Volatile.Read(ref _fields[field])
            : Interlocked.CompareExchange(ref _fields[field], 0, 0);
        private void Store(int field, long value) => Interlocked.Exchange(ref _fields[field], value);
        private static long NewIdentity() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) | 1;

        // AcquirePointer owns an extra SafeHandle reference. FileStream/view finalizers
        // cannot release it for us when a caller forgets to dispose the connection.
        ~SharedCoordinationPage() { this.Dispose(); }

        public void Dispose()
        {
            lock (_writeLock) this.Close();
            GC.SuppressFinalize(this);
        }

        private void Close()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pointerAcquired)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointerAcquired = false;
            }
            this.CloseView();
            _map?.Dispose();
            _participation?.Dispose();
        }

        private void CloseView()
        {
            // The extra pointer reference was released above. Closing the native
            // view first prevents the accessor's explicit Dispose from flushing
            // ephemeral control bytes, including on the finalizer thread. Live
            // peers retain their own coherent views; restart never trusts this page.
            _view?.SafeMemoryMappedViewHandle.Dispose();
            _view?.Dispose();
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
