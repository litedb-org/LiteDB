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
    /// only where F_FULLFSYNC is unsupported), fsync elsewhere. Windows keeps FlushFileBuffers, which throws.
    /// </summary>
    internal static class NativeFileSync
    {
        private const int EINTR = 4;
        private const int EINVAL = 22;
        private const int ENOTTY = 25;
        private const int ENOTSUP_BSD = 45;
        private const int EOPNOTSUPP_BSD = 102;
        private const int F_FULLFSYNC = 51;
        private const int O_RDONLY = 0;

        private static readonly bool _windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool _macOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static readonly bool _bsd = _macOS || RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD"));
        private static volatile bool _nativeUnavailable;

#if DEBUG || TESTING
        /// <summary>Test hook: returns the errno a native sync of this file reports (0 = success).</summary>
        internal static Func<string, int> SimulateErrno;

        /// <summary>Test hook: returns the errno a sync of this directory reports (0 = success), on every platform.</summary>
        internal static Func<string, int> SimulateDirectoryErrno;

        /// <summary>Test hook: behave as if no C library could be bound (file syncs go through Flush(true)).</summary>
        internal static volatile bool SimulateRuntimeSync;
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
            // A caller's FileStream subclass that overrides Flush(bool) defines its own sync.
            if (_windows || _nativeUnavailable || RuntimeSyncSimulated || OverridesFlush(stream.GetType()))
            {
                stream.Flush(true);
                return;
            }

            if (!NativeLibc.TryGet(out var fsync, out var fcntl))
            {
                // No resolvable C library: keep the runtime's sync, which cannot report
                // failures. Observable through UsesRuntimeSync.
                _nativeUnavailable = true;
                stream.Flush(true);
                return;
            }

            // Push managed buffers to the OS, then sync the descriptor ourselves.
            stream.Flush(false);
            var errno = Sync(stream.SafeFileHandle, fsync, fcntl);
            if (errno != 0) throw new FileSyncException(stream.Name, errno, _bsd);
        }

        private static bool OverridesFlush(Type type)
        {
            if (type == typeof(FileStream)) return false;
            return _overrides.GetOrAdd(type, t =>
                t.GetMethod(nameof(FileStream.Flush), new[] { typeof(bool) })?.DeclaringType != typeof(FileStream));
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _overrides =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// True when Unix device syncs go through the runtime's Flush(true), which reports
        /// no errors, because no C library could be bound. Always false on Windows. Such a
        /// sync is still attempted, but it cannot prove durability.
        /// </summary>
        internal static bool UsesRuntimeSync =>
            RuntimeSyncSimulated || (!_windows && (_nativeUnavailable || NativeLibc.LibraryName == null));

#if DEBUG || TESTING
        private static bool RuntimeSyncSimulated => SimulateRuntimeSync;
#else
        private const bool RuntimeSyncSimulated = false;
#endif

        /// <summary>
        /// Make the entries of <paramref name="directory"/> durable (Unix: fsync of the directory).
        /// Syncing a file does not persist a newly created name there. Windows needs nothing: the
        /// file's FlushFileBuffers commits NTFS metadata, including the creation. Without a C
        /// library the sync cannot run, which is reported like storage that cannot sync.
        /// </summary>
        internal static void SyncDirectory(string directory)
        {
#if DEBUG || TESTING
            var simulate = SimulateDirectoryErrno;
            if (simulate != null)
            {
                var injected = simulate(directory);
                if (injected != 0) throw new FileSyncException(directory, injected, _bsd);
                return;
            }
#endif
            if (_windows) return;
            if (RuntimeSyncSimulated || !NativeLibc.TryGetDirectorySync(out var open, out var fsync, out var close))
                throw FileSyncException.Unavailable(directory);

            int descriptor;
            while ((descriptor = open(directory, O_RDONLY)) < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno != EINTR) throw new FileSyncException(directory, errno, _bsd);
            }
            try
            {
                var failure = Retry(() => fsync(descriptor));
                if (failure != 0) throw new FileSyncException(directory, failure, _bsd);
            }
            finally { close(descriptor); }
        }

        private static int Sync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, NativeLibc.FsyncCall fsync, NativeLibc.FcntlCall fcntl)
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var fd = handle.DangerousGetHandle().ToInt32();
                if (_macOS)
                {
                    // Fall back to fsync only where F_FULLFSYNC is unsupported (some file
                    // systems and handles). A genuine device error must not be hidden by an
                    // fsync that may only reach the drive's write cache.
                    var full = Retry(() => fcntl(fd, F_FULLFSYNC));
                    if (full != ENOTSUP_BSD && full != EOPNOTSUPP_BSD && full != EINVAL && full != ENOTTY) return full;
                }
                return Retry(() => fsync(fd));
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

        private FileSyncException(string message) : base(message)
        {
            IsUnsupported = true;
        }

        /// <summary>No C library to issue the sync with: treated as storage that cannot sync.</summary>
        internal static FileSyncException Unavailable(string path) =>
            new FileSyncException($"Cannot sync '{path}': no C library could be bound.");

        internal int Errno { get; }

        /// <summary>
        /// True when the answer means "this file cannot be synced" (#2242), not that a sync failed.
        /// </summary>
        internal bool IsUnsupported { get; }
    }
}
