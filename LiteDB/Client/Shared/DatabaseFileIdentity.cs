using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Client.Shared
{
    internal static class DatabaseFileIdentity
    {
        internal static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool Darwin => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        internal static SafeFileHandle Open(string filename, bool readOnly, bool create)
        {
            RequirePlatform();
            SafeFileHandle handle;
            if (Windows)
            {
                // LockFileEx needs only read access. Sharing delete permits rebuild's
                // rename while the old inode remains locked until its final lease ends.
                handle = CreateFileW(WindowsPath(filename), 0x80000000, 7, IntPtr.Zero, create ? 4u : 3u, 0, IntPtr.Zero);
            }
            else
            {
                var flags = (readOnly ? 0 : 2) | (Darwin ? 0x1000000 : 0x80000); // O_CLOEXEC
                if (create) flags |= Darwin ? 0x200 : 0x40;
                var descriptor = DatabaseUnixNative.Api.Open(filename, flags, 0x1b6); // 0666 + umask
                handle = new SafeFileHandle((IntPtr)descriptor, true);
            }
            if (!handle.IsInvalid) return handle;
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (code == (Windows ? 3 : 2) && !Directory.Exists(System.IO.Path.GetDirectoryName(filename)))
                throw new DirectoryNotFoundException("Database directory does not exist: " + filename);
            if (code == 2) throw new FileNotFoundException("Database does not exist.", filename);
            if (code == (Windows ? 5 : 13)) throw new UnauthorizedAccessException("Database access denied: " + filename);
            if (code == (Windows ? 206 : Darwin ? 63 : 36)) throw new PathTooLongException("Database path is too long: " + filename);
            throw DatabaseFileLock.Error("Opening database admission handle");
        }

        internal static string Read(SafeFileHandle handle)
        {
            if (Windows)
            {
                if (!GetFileInformationByHandle(handle, out var info)) throw DatabaseFileLock.Error("File identity");
                RequireSingleLink(info.Links);
                // ReFS uses 128-bit IDs; the legacy 64-bit file index is insufficient.
                var identity = new byte[24]; // FILE_ID_INFO: volume serial + FILE_ID_128
                if (!GetFileInformationByHandleEx(handle, 18, identity, identity.Length))
                    throw DatabaseFileLock.Error("128-bit file identity");
                return BitConverter.ToString(identity);
            }
            // Only the supported 64-bit Unix ABIs reach here. Reserve more than
            // sizeof(stat); never marshal an OS structure using another OS's layout.
            var bytes = new byte[256];
            var result = DatabaseUnixNative.Stat(handle, bytes);
            if (result != 0) throw DatabaseFileLock.Error("fstat");
            var links = Darwin ? BitConverter.ToUInt16(bytes, 6) :
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? BitConverter.ToUInt32(bytes, 20) :
                BitConverter.ToUInt64(bytes, 16);
            RequireSingleLink(links);
            var device = Darwin ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt64(bytes, 0);
            return device.ToString("X16") + "-" + BitConverter.ToUInt64(bytes, 8).ToString("X16");
        }

        private static void RequireSingleLink(ulong links)
        {
            if (links != 1) throw new IOException("Database admission requires one filesystem link. " +
                "Hard-linked databases cannot safely share path-based WAL and recovery files.");
        }

        internal static string CanonicalPath(string filename)
        {
            RequirePlatform();
            filename = System.IO.Path.GetFullPath(filename);
            if (!Windows)
            {
                var pointer = DatabaseUnixNative.Api.RealPath(filename, IntPtr.Zero);
                if (pointer != IntPtr.Zero)
                {
                    try { return Marshal.PtrToStringAnsi(pointer); }
                    finally { DatabaseUnixNative.Api.Free(pointer); }
                }
                var code = Marshal.GetLastWin32Error();
                if (code == 13) throw new UnauthorizedAccessException("Database path access denied: " + filename);
                if (code == (Darwin ? 63 : 36)) throw new PathTooLongException("Database path is too long: " + filename);
                if (code == 20) throw new DirectoryNotFoundException("Database directory does not exist: " + filename);
                if (code != 2) throw DatabaseFileLock.Error("realpath");
                // A dangling file symlink can point into a guarded rebuild gap.
                // Treating it as a new filename would create the target while
                // consulting the alias's unrelated WAL/recovery marker names.
                if (DatabaseUnixNative.Api.ReadLink(filename, out _, (UIntPtr)1).ToInt64() >= 0)
                    throw new IOException("Cannot admit a database through an unresolved symlink: " + filename);
                var linkError = Marshal.GetLastWin32Error();
                if (linkError != 2 && linkError != 22) throw DatabaseFileLock.Error("readlink");
            }
            else
            {
                using var handle = CreateFileW(WindowsPath(filename), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var path = new StringBuilder(32768);
                    var size = GetFinalPathNameByHandleW(handle, path, path.Capacity, 0);
                    if (size == 0 || size >= path.Capacity) throw DatabaseFileLock.Error("Canonical database path");
                    var value = path.ToString();
                    return value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
                        ? @"\\" + value.Substring(8) : value.StartsWith(@"\\?\", StringComparison.Ordinal)
                        ? value.Substring(4) : value;
                }
                var error = Marshal.GetLastWin32Error();
                if (error == 5) throw new UnauthorizedAccessException("Database path access denied: " + filename);
                if (error == 206) throw new PathTooLongException("Database path is too long: " + filename);
                if (error != 2 && error != 3) throw DatabaseFileLock.Error("Canonical database path");
                using var link = CreateFileW(WindowsPath(filename), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (!link.IsInvalid)
                    throw new IOException("Cannot admit a database through an unresolved reparse point: " + filename);
                var linkError = Marshal.GetLastWin32Error();
                if (linkError != 2 && linkError != 3) throw DatabaseFileLock.Error("Reparse point inspection");
            }
            var parent = System.IO.Path.GetDirectoryName(filename);
            if (string.IsNullOrEmpty(parent) || parent == filename || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("Database directory does not exist: " + filename);
            return System.IO.Path.Combine(CanonicalPath(parent), System.IO.Path.GetFileName(filename));
        }

        internal static void RequireLocalVolume(SafeFileHandle handle, string filename)
        {
#if DEBUG || TESTING
            if (UnsupportedVolume?.Invoke(filename) == true) throw new IOException("Unsupported database locking filesystem.");
#endif
            if (Windows)
            {
                var root = new StringBuilder(32768);
                var format = new StringBuilder(64);
                if (!GetVolumePathNameW(filename, root, root.Capacity) || GetDriveTypeW(root.ToString()) == 4 ||
                    !GetVolumeInformationW(root.ToString(), null, 0, out _, out _, out _, format, format.Capacity) ||
                    (format.ToString() != "NTFS" && format.ToString() != "ReFS"))
                    throw new IOException("Database admission requires a local NTFS or ReFS volume.");
                return;
            }
            var bytes = new byte[4096];
            var result = DatabaseUnixNative.Api.FileSystemStat(handle, bytes);
            if (result != 0) throw DatabaseFileLock.Error("fstatfs");
            if (Darwin)
            {
                var type = Encoding.ASCII.GetString(bytes, 72, 16).TrimEnd('\0');
                if (type == "apfs" || type == "hfs") return;
            }
            else
            {
                var type = BitConverter.ToUInt32(bytes, 0);
                // Local filesystems with OFD locking, including local test/scratch storage.
                if (type == 0xef53 || type == 0x58465342 || type == 0x9123683e ||
                    type == 0x01021994 || type == 0x794c7630) return;
            }
            throw new IOException("Database admission is unsupported on this filesystem; use a supported local volume.");
        }

        private static void RequirePlatform()
        {
            if (Windows) return;
            var architecture = RuntimeInformation.ProcessArchitecture;
            if ((!Darwin && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) ||
                (architecture != Architecture.X64 && architecture != Architecture.Arm64))
                throw new PlatformNotSupportedException("Database admission requires Windows, or 64-bit Linux/macOS with OFD locks.");
        }

        private static string WindowsPath(string filename) => filename.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? filename : filename.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + filename.Substring(2) : @"\\?\" + filename;

#if DEBUG || TESTING
        internal static Func<string, bool> UnsupportedVolume;
#endif
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] byte[] info, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int size, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathNameW(string filename, StringBuilder root, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationW(string root, StringBuilder name, int size,
            out uint serial, out uint componentLength, out uint flags, StringBuilder format, int formatSize);
    }
}
