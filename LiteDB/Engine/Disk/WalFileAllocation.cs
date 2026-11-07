using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB.Engine
{
    /// <summary>
    /// Optional Linux extent reservation. KEEP_SIZE leaves the logical WAL and
    /// its compatibility padding unchanged; recovery never interprets capacity
    /// as frames. Failure disables this hint and ordinary writes report I/O errors.
    /// </summary>
    internal sealed class WalFileAllocation
    {
        internal const int ChunkSize = 4 * 1024 * 1024;
        private long _reserved;
        private bool _disabled;

#if NET8_0_OR_GREATER
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int Allocate(int descriptor, int mode, long offset, long length);
        private static readonly Allocate _allocate = Resolve();

        private static Allocate Resolve()
        {
            if (IntPtr.Size != 8 || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
            var name = NativeLibc.LibraryName;
            if (name == null || !NativeLibrary.TryLoad(name, out var library)) return null;
            if (NativeLibrary.TryGetExport(library, "fallocate", out var address))
                return Marshal.GetDelegateForFunctionPointer<Allocate>(address);
            NativeLibrary.Free(library);
            return null;
        }
#endif

        internal void Reserve(Stream stream, long end)
        {
#if NET8_0_OR_GREATER
            if (_disabled || end <= _reserved || _allocate == null || stream.GetType() != typeof(FileStream)) return;
            var file = (FileStream)stream;
            var target = checked((end + ChunkSize - 1) / ChunkSize * ChunkSize);
            var handle = file.SafeFileHandle;
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                // FALLOC_FL_KEEP_SIZE: reserve extents, including beyond EOF,
                // without creating a logical tail or changing existing bytes.
                if (_allocate(handle.DangerousGetHandle().ToInt32(), 1, _reserved, target - _reserved) != 0)
                    _disabled = true;
                else _reserved = target;
            }
            finally { if (added) handle.DangerousRelease(); }
#endif
        }

        internal void Reset() => _reserved = 0;
    }
}
