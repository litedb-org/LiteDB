#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Lifetime is owned by the participant, which serializes release with leases.
    internal sealed unsafe class SharedMemoryMap : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private byte* _pointer;
        private bool _acquired;
        internal readonly long Size;

        internal SharedMemoryMap(FileStream file, long size)
        {
            Size = size;
            _map = MemoryMappedFile.CreateFromFile(file, null, size, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: true);
            try
            {
                _view = _map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _acquired = true;
                _pointer = pointer + _view.PointerOffset;
                if (((long)_pointer & 7) != 0) throw new IOException("Unaligned Shared mapping.");
            }
            catch { this.Dispose(); throw; }
        }

        internal long Load(long offset) { this.Require(offset, 8); return Interlocked.Read(ref *(long*)(_pointer + offset)); }
        internal void Store(long offset, long value) { this.Require(offset, 8); Interlocked.Exchange(ref *(long*)(_pointer + offset), value); }
        internal byte[] Read(long offset, int count)
        {
            this.Require(offset, count);
            var result = new byte[count];
            _view.ReadArray(offset, result, 0, count);
            return result;
        }
        internal long CompareExchange(long offset, long value, long expected)
        { this.Require(offset, 8); return Interlocked.CompareExchange(ref *(long*)(_pointer + offset), value, expected); }
        internal bool Matches(long offset, byte[] expected)
        {
            for (var i = 0; i < expected.Length; i += 8)
                if (Load(offset + i) != BitConverter.ToInt64(expected, i)) return false;
            return true;
        }
        internal void Write(long offset, byte[] bytes)
        {
            this.Require(offset, bytes.Length);
            _view.WriteArray(offset, bytes, 0, bytes.Length);
        }
        internal void Zero(long offset, long count)
        {
            this.Require(offset, count);
            new Span<byte>(_pointer + offset, checked((int)count)).Clear();
        }
        private void Require(long offset, long count)
        {
            if (!_acquired) throw new ObjectDisposedException(nameof(SharedMemoryMap));
            if (offset < 0 || count < 0 || offset > Size - count) throw new ArgumentOutOfRangeException(nameof(offset));
        }
        ~SharedMemoryMap() { this.Dispose(); }
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_acquired) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _acquired = false; _pointer = null; }
            _view?.Dispose();
            _map?.Dispose();
        }
    }
}
#endif
