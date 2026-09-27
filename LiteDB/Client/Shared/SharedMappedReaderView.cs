#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>A fixed-size view. Owners never resize a published table.</summary>
    internal sealed unsafe class SharedMappedReaderView : IDisposable
    {
        private readonly FileStream _file;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly long* _fields;
        private bool _pointerAcquired;
        private bool _disposed;

        private SharedMappedReaderView(FileStream file)
        {
            _file = file;
            try
            {
                if (file.Length != SharedMappedReaderSlots.Size) throw new IOException("Unknown mapped reader table size.");
                _map = MemoryMappedFile.CreateFromFile(file, null, SharedMappedReaderSlots.Size,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                _view = _map.CreateViewAccessor(0, SharedMappedReaderSlots.Size, MemoryMappedFileAccess.ReadWrite);
                byte* address = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
                _pointerAcquired = true;
                _fields = (long*)(address + _view.PointerOffset);
                if (((long)_fields & 7) != 0) throw new IOException("Unaligned mapped reader table.");
            }
            catch { this.Dispose(); throw; }
        }

        internal static SharedMappedReaderView Create(string path)
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            try
            {
                // Allocate the complete page before exposing a lease; disk-full is an
                // ordinary creation failure, not a first-touch allocation during admission.
                file.Write(new byte[SharedMappedReaderSlots.Size]);
                return new SharedMappedReaderView(file);
            }
            catch { file.Dispose(); throw; }
        }

        internal static SharedMappedReaderView Open(string path) => new SharedMappedReaderView(
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None));

        internal long Load(int index) => IntPtr.Size == 8
            ? Volatile.Read(ref _fields[index]) : Interlocked.CompareExchange(ref _fields[index], 0, 0);
        internal void Store(int index, long value) => Interlocked.Exchange(ref _fields[index], value);

        ~SharedMappedReaderView() { this.Dispose(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pointerAcquired)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointerAcquired = false;
            }
            // Ephemeral control bytes need coherence, not a durability flush on close.
            _view?.SafeMemoryMappedViewHandle.Dispose();
            _view?.Dispose();
            _map?.Dispose();
            _file?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
#endif
