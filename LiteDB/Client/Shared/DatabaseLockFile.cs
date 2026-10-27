using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Client.Shared
{
    // Darwin has a common conflict domain for OFD byte locks and flock. Keep
    // admission away from the data inode's FileStream sharing locks. The sibling
    // name depends on physical identity, so replacement carries its lock with it.
    internal static class DatabaseLockFile
    {
        internal static string Filename(string database, string identity) =>
            Path.Combine(Path.GetDirectoryName(database), ".litedb-admission", identity + ".lock");

        internal static SafeFileHandle Open(string database, string identity, bool readOnly)
        {
            var filename = Filename(database, identity);
            var directory = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(directory);
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Database admission lock directory must be a real directory.");
            var darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            // O_CREAT, O_CLOEXEC, O_NOFOLLOW, O_NONBLOCK; never truncate or unlink.
            // Unlinking even after unlock would let a racing opener lock an old
            // inode while a new opener creates and locks a different one.
            var flags = (readOnly ? 0 : 2) | (darwin ? 0x1000304 : LinuxOpenFlags());
            var descriptor = DatabaseUnixNative.Api.Open(filename, flags, 0x1b6);
            var handle = new SafeFileHandle((IntPtr)descriptor, true);
            try
            {
                if (handle.IsInvalid) throw DatabaseFileLock.Error("Opening database admission lock file");
                var stat = new byte[256];
                if (DatabaseUnixNative.Stat(handle, stat) != 0) throw DatabaseFileLock.Error("Admission lock file fstat");
                var modeOffset = darwin ? 4 : LinuxStatModeOffset();
                if ((BitConverter.ToUInt16(stat, modeOffset) & 0xf000) != 0x8000)
                    throw new IOException("Database admission lock file must be a regular file.");
                DatabaseFileIdentity.Read(handle); // Reject hard-linked lock files too.
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static int LinuxOpenFlags()
        {
            // Linux keeps O_CLOEXEC and O_NONBLOCK stable, but O_NOFOLLOW is
            // 0x20000 on x86 and 0x8000 on the asm-generic ABIs (arm64/arm).
            // Using the x86 value on arm silently follows a lock-file symlink.
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                case Architecture.X86:
                    return 0xa0840;
                case Architecture.Arm64:
                case Architecture.Arm:
                    return 0x88840;
                default:
                    throw new PlatformNotSupportedException("Unsupported Linux process architecture for database admission.");
            }
        }

        private static int LinuxStatModeOffset() =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm ? 16 : 24;
    }
}
