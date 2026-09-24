using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB.Engine
{
    internal partial class FileStreamFactory
    {
        /// <summary>
        /// On Unix, syncing the file alone does not persist a newly created WAL's
        /// directory entry. Promotion must make that name durable before overwriting
        /// the sole data header. Windows uses the preceding FlushFileBuffers call.
        /// </summary>
        internal void SyncDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var directory = Path.GetDirectoryName(Path.GetFullPath(_filename));
            var descriptor = NativeDirectorySync.Open(directory, 0); // O_RDONLY on Unix
            if (descriptor < 0) throw DirectorySyncError(directory);
            try
            {
                while (NativeDirectorySync.Fsync(descriptor) != 0)
                {
                    if (Marshal.GetLastWin32Error() == 4) continue; // EINTR
                    throw DirectorySyncError(directory);
                }
            }
            finally { NativeDirectorySync.Close(descriptor); }
        }

        private static IOException DirectorySyncError(string directory) => new IOException(
            "Cannot durably publish the promotion recovery WAL in " + directory,
            new Win32Exception(Marshal.GetLastWin32Error()));

        private static class NativeDirectorySync
        {
            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags);

            [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
            internal static extern int Fsync(int descriptor);

            [DllImport("libc", EntryPoint = "close", SetLastError = true)]
            internal static extern int Close(int descriptor);
        }
    }
}
