using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Engine
{
    internal sealed partial class FileOwnership
    {
        private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));

        private static bool IsDarwin => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("TVOS")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("WATCHOS")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("MACCATALYST"));

        private static bool IsVirtualRuntime => RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("WASI"));

        // OFD locks were added in Linux 3.15. Choose a consistent protocol for
        // every engine in this process on older kernels, before opening streams.
        private static bool UseLegacyLinuxLocks => IsLinux && Environment.OSVersion.Version < new Version(3, 15);

        internal static FileStream OpenFile(string path, FileMode mode, FileAccess access,
            FileShare share, int bufferSize, FileOptions options)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IsVirtualRuntime ||
                IsLinux && !UseLegacyLinuxLocks || IsDarwin)
                return new FileStream(path, mode, access, share, bufferSize, options);

            // Other POSIX runtimes (including FreeBSD) use flock for ownership.
            // FileStream(path) would take a second flock and conflict with our
            // own exclusive owner. Open the descriptor directly for these files.
            // This helper is only used with Open and OpenOrCreate.
            if (mode == FileMode.OpenOrCreate && !File.Exists(path))
            {
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            }
            // Set close-on-exec atomically; fork can run on another thread
            // between open and a later fcntl(F_SETFD).
            var closeOnExec = IsLinux ? 0x80000 : 0x00100000; // Linux / FreeBSD
            var descriptor = UnixOpen(path, (access == FileAccess.Read ? 0 : 2) | closeOnExec);
            if (descriptor < 0) throw CreateOpenError(path, Marshal.GetLastWin32Error());
            var handle = new SafeFileHandle((IntPtr)descriptor, true);
            try
            {
                return new FileStream(handle, access, bufferSize, false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static IOException CreateOpenError(string path, int error)
        {
            var native = new Win32Exception(error);
            if (error == 2) return new FileNotFoundException("Database file not found.", path, native);
            if (error == 20) return new DirectoryNotFoundException("Invalid database directory: " + path, native);
            return new IOException("Unable to open database file: " + path, native);
        }

        private static IOException NativeError(string message) =>
            new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int UnixOpen(string path, int flags);
    }
}
