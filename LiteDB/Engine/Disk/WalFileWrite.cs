#if NET8_0_OR_GREATER
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Completed writes on the Windows overlapped WAL handle without a per-write
    /// threadpool callback/event allocation. The WAL writer monitor owns this object.
    /// The same handle remains available to the independent captured device barrier.
    /// </summary>
    internal sealed class WalFileWrite : IDisposable
    {
        private readonly EventWaitHandle _completion = new EventWaitHandle(false, EventResetMode.ManualReset);

        [StructLayout(LayoutKind.Sequential)]
        private struct Overlapped
        {
            internal UIntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool WriteFile(IntPtr file, byte* buffer, uint count,
            IntPtr written, ref Overlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr file, ref Overlapped overlapped,
            out uint transferred, bool wait);

        internal unsafe void Write(FileStream file, byte[] buffer, int offset, int count)
        {
            // SafeFileHandle exposure flushes any managed buffer and disables cached
            // length assumptions. All engine frame writes are larger than that buffer.
            var handle = file.SafeFileHandle;
            var position = file.Position;
            var added = false;
            var eventAdded = false;
            var completion = _completion.SafeWaitHandle;
            try
            {
                handle.DangerousAddRef(ref added);
                completion.DangerousAddRef(ref eventAdded);
                var overlapped = new Overlapped
                {
                    Offset = (uint)position,
                    OffsetHigh = (uint)(position >> 32),
                    // Windows' documented low-bit protocol suppresses IOCP packets,
                    // including when FileStream has already bound this handle to IOCP.
                    Event = new IntPtr(completion.DangerousGetHandle().ToInt64() | 1)
                };
                fixed (byte* bytes = &buffer[offset])
                {
                    if (!WriteFile(handle.DangerousGetHandle(), bytes, (uint)count, IntPtr.Zero, ref overlapped))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != 997) throw Error(error);
                    }
                    // Never release the pin, event or stack OVERLAPPED before terminal
                    // completion, even if the initial WriteFile returned IO_PENDING.
                    if (!GetOverlappedResult(handle.DangerousGetHandle(), ref overlapped, out var written, true))
                        throw Error(Marshal.GetLastWin32Error());
                    if (written != count) throw new IOException("Incomplete WAL file write.");
                }
                file.Position = checked(position + count);
            }
            finally
            {
                if (eventAdded) completion.DangerousRelease();
                if (added) handle.DangerousRelease();
            }
        }

        private static IOException Error(int error) => new IOException(
            "WAL file write failed: " + new Win32Exception(error).Message,
            unchecked((int)(0x80070000u | (uint)error)));

        public void Dispose() => _completion.Dispose();
    }
}
#endif
