using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Engine
{
    internal sealed partial class FileOwnership
    {
        private static void VerifyPath(FileStream owner, string path)
        {
            // An opener can pause before locking while another engine atomically
            // replaces the file during rebuild. Never protect the old inode while
            // DiskService subsequently opens the replacement by its pathname.
            using (var current = OpenFile(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
            {
                var held = GetIdentity(owner.SafeFileHandle);
                var named = GetIdentity(current.SafeFileHandle);
                if (held != named)
                    throw new LiteException(0, "Database file changed while acquiring ownership. Retry opening the database.");
            }
        }

        private static string GetIdentity(SafeFileHandle handle)
        {
            var buffer = new byte[256];
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (GetFileInformationByHandleEx(handle, 18, buffer, 24))
                    return BitConverter.ToString(buffer, 0, 24);
                if (!GetFileInformationByHandle(handle, buffer)) throw NativeError("Unable to identify database file.");
                return BitConverter.ToString(buffer, 28, 4) + BitConverter.ToString(buffer, 44, 8);
            }

            int result;
            if (IsDarwin)
            {
                try { result = DarwinFStat(handle, buffer); }
                catch (EntryPointNotFoundException) { result = FStat(handle, buffer); }
                if (result != 0) throw NativeError("Unable to identify database file.");
                // Darwin stat64: dev_t at 0 (32 bits), ino_t at 8 (64 bits).
                return BitConverter.ToString(buffer, 0, 4) + BitConverter.ToString(buffer, 8, 8);
            }
            if (IsLinux && IntPtr.Size == 4)
            {
                // statx has a kernel-defined layout independent of libc's time_t
                // and large-file ABI. It is available on Linux 4.11 and newer.
                var number = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? 383 : 397;
                result = (int)Statx((IntPtr)number, handle, "", 0x1000, 0x100, buffer);
                if (result == 0)
                    return BitConverter.ToString(buffer, 136, 8) + BitConverter.ToString(buffer, 32, 8);
                var error = Marshal.GetLastWin32Error();
                // Older container profiles may deny statx even when fstat64 is
                // permitted. Bad handles and actual I/O failures still propagate.
                if (error != 38 && error != 1 && error != 13 && error != 95 && error != 22)
                    throw NativeError("Unable to identify database file.");
                // Use the kernel's stat64 layout, including on 32-bit musl
                // whose libc stat layout can differ (for example time64).
                result = (int)FStat64((IntPtr)197, handle, buffer);
                if (result != 0) throw NativeError("Unable to identify database file.");
                var inodeOffset = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? 88 : 96;
                return BitConverter.ToString(buffer, 0, 8) + BitConverter.ToString(buffer, inodeOffset, 8);
            }
            result = FStat(handle, buffer);
            if (result != 0) throw NativeError("Unable to identify database file.");
            // LP64 Linux and current FreeBSD place dev_t and ino_t first.
            return BitConverter.ToString(buffer, 0, 16);
        }

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(SafeFileHandle handle, [Out] byte[] status);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern IntPtr FStat64(IntPtr number, SafeFileHandle handle, [Out] byte[] status);
        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int DarwinFStat(SafeFileHandle handle, [Out] byte[] status);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern IntPtr Statx(IntPtr number, SafeFileHandle handle, string path,
            int flags, uint mask, [Out] byte[] status);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, [Out] byte[] information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
            [Out] byte[] information, uint size);
    }
}
