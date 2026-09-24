using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB
{
    /// <summary>
    /// A device sync that reports its failures. On Unix, released .NET runtimes lose every
    /// fsync error: SystemNative_FSync assigns <c>fsync(fd) &lt; 0</c> instead of the result
    /// (fixed only in dotnet/runtime#124725), and the managed layer additionally ignores
    /// EROFS/EINVAL/ENOTSUP. FileStream.Flush(true) therefore reports success after EIO.
    /// Unix handles are synced here directly: F_FULLFSYNC on macOS (falling back to fsync
    /// like the fixed runtime), fsync elsewhere. Windows keeps FlushFileBuffers, which throws.
    /// </summary>
    internal static class NativeFileSync
    {
        private const int EINTR = 4;
        private const int F_FULLFSYNC = 51;

        private static readonly bool _windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool _macOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static readonly bool _bsd = _macOS || RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD"));
        private static volatile bool _nativeUnavailable;

#if DEBUG || TESTING
        /// <summary>Test hook: returns the errno a native sync of this file reports (0 = success).</summary>
        internal static Func<string, int> SimulateErrno;
#endif

        internal static void FlushToDisk(FileStream stream)
        {
#if DEBUG || TESTING
            var simulate = SimulateErrno;
            if (simulate != null)
            {
                stream.Flush(true);
                var injected = simulate(stream.Name);
                if (injected != 0) throw new FileSyncException(stream.Name, injected, _bsd);
                return;
            }
#endif
            if (_windows || _nativeUnavailable)
            {
                stream.Flush(true);
                return;
            }

            // Push managed buffers to the OS, then sync the descriptor ourselves.
            stream.Flush(false);
            int errno;
            try
            {
                errno = Sync(stream.SafeFileHandle);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No resolvable libc: keep the runtime's sync, which cannot report failures.
                _nativeUnavailable = true;
                stream.Flush(true);
                return;
            }
            if (errno != 0) throw new FileSyncException(stream.Name, errno, _bsd);
        }

        private static int Sync(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var fd = handle.DangerousGetHandle().ToInt32();
                if (_macOS && Retry(() => Native.FcntlNoArgument(fd, F_FULLFSYNC)) == 0) return 0;

                // Not every file system or handle supports F_FULLFSYNC; a genuine I/O
                // error fails fsync as well.
                return Retry(() => Native.Fsync(fd));
            }
            finally
            {
                if (added) handle.DangerousRelease();
            }
        }

        private static int Retry(Func<int> call)
        {
            while (true)
            {
                if (call() == 0) return 0;
                var errno = Marshal.GetLastWin32Error();
                if (errno != EINTR) return errno;
            }
        }

        private static class Native
        {
            [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
            internal static extern int Fsync(int descriptor);

            // fcntl is variadic; F_FULLFSYNC takes no argument, so only the fixed parameters are passed.
            [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int FcntlNoArgument(int descriptor, int command);
        }
    }

    /// <summary>A failed device sync of a file, carrying the raw errno as HResult.</summary>
    internal sealed class FileSyncException : IOException
    {
        private const int EINVAL = 22;
        private const int EROFS = 30;
        private const int ENOTSUP_BSD = 45;
        private const int EOPNOTSUPP_BSD = 102;
        private const int ENOTSUP_LINUX = 95;

        internal FileSyncException(string path, int errno, bool bsd)
            : base($"Device sync of '{path}' failed (errno {errno}).", errno)
        {
            Errno = errno;
            IsUnsupported = errno == EINVAL || errno == EROFS ||
                (bsd ? errno == ENOTSUP_BSD || errno == EOPNOTSUPP_BSD : errno == ENOTSUP_LINUX);
        }

        internal int Errno { get; }

        /// <summary>
        /// True when the answer means "this file cannot be synced" (#2242), not that a sync failed.
        /// </summary>
        internal bool IsUnsupported { get; }
    }
}
