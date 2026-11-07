using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Client.Shared
{
    internal static class DatabaseVolumePolicy
    {
        internal static bool IsNative(SafeFileHandle handle, string filename, bool allowFallback)
        {
            var native = false;
            var local = false;
            if (DatabaseFileIdentity.Windows)
            {
                var root = new StringBuilder(32768);
                var format = new StringBuilder(64);
                if (!GetVolumePathNameW(DatabaseFileIdentity.WindowsPath(filename), root, root.Capacity) ||
                    !GetVolumeInformationW(root.ToString(), null, 0, out _, out _, out _, format, format.Capacity))
                    throw DatabaseFileLock.Error("Database volume inspection");
                var drive = GetDriveTypeW(root.ToString());
                local = drive == 2 || drive == 3 || drive == 6; // removable, fixed, RAM
                native = local && (format.ToString() == "NTFS" || format.ToString() == "ReFS");
            }
            else
            {
                var bytes = new byte[4096];
                if (DatabaseUnixNative.Api.FileSystemStat(handle, bytes) != 0) throw DatabaseFileLock.Error("fstatfs");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var type = Encoding.ASCII.GetString(bytes, 72, 16).TrimEnd('\0');
                    local = (BitConverter.ToUInt32(bytes, 64) & 0x1000) != 0; // MNT_LOCAL
                    native = local && (type == "apfs" || type == "hfs");
                }
                else
                {
                    var type = BitConverter.ToUInt32(bytes, 0);
                    native = type == 0xef53 || type == 0x58465342 || type == 0x9123683e ||
                        type == 0x01021994 || type == 0x794c7630;
                    local = native;
                    if (!native && allowFallback) local = LinuxLocalMount(handle, filename, type);
                }
            }
#if DEBUG || TESTING
            if (DatabaseFileIdentity.UnsupportedVolume?.Invoke(filename) == true) native = false;
#endif
            if (native) return true;
            if (allowFallback && local)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    RequireDatabaseMount(filename);
                return false;
            }
            throw new IOException("Database admission requires a qualified local filesystem. " +
                "" +
                "Network storage and volumes of unknown locality are unsupported.");
        }

        private static string[] Mount(string filename)
        {
            // Inspect the current mount namespace. Unknown or inaccessible topology
            // cannot authorize a fallback. Escapes are defined by proc mountinfo.
            return File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' '))
                .Where(parts => parts.Length > 8 && (filename == Unescape(parts[4]) ||
                    filename.StartsWith(Unescape(parts[4]).TrimEnd('/') + "/", StringComparison.Ordinal)))
                .OrderByDescending(parts => Unescape(parts[4]).Length).FirstOrDefault()
                ?? throw new IOException("Cannot establish the database mount's locality.");
        }

        private static string Unescape(string value) => value.Replace("\\040", " ")
            .Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

        internal static void RequireDatabaseMount(string filename)
        {
            if (DatabaseMountIdentity.IsFileMount(filename) ?? (Unescape(Mount(filename)[4]) == filename))
                throw new IOException("File-only bind mounts cannot preserve the database/WAL namespace.");
        }

        private static bool LinuxLocalMount(SafeFileHandle handle, string filename, uint type)
        {
            var mount = Mount(filename);
            var stat = new byte[256];
            if (DatabaseUnixNative.Stat(handle, stat) != 0) throw DatabaseFileLock.Error("fstat mount identity");
            var device = BitConverter.ToUInt64(stat, 0);
            var major = ((device >> 8) & 0xfff) | ((device >> 32) & 0xfffff000);
            var minor = (device & 0xff) | ((device >> 12) & 0xffffff00);
            if (mount[2] != major + ":" + minor)
                throw new IOException("Database mount changed during locality inspection.");
            var separator = Array.IndexOf(mount, "-");
            if (separator < 0 || separator + 2 >= mount.Length) return false;
            var format = mount[separator + 1];
            // ZFS uses virtual device numbers. Its in-kernel filesystem type is
            // local; FUSE implementations remain unqualified. Other local formats
            // need a block device represented by this kernel, not a remote denylist.
            if (format == "zfs" && type == 0x2fc12fc1) return true;
            if (format.StartsWith("fuse", StringComparison.Ordinal) || format == "9p" || format == "virtiofs") return false;
            // Positive single-host format knowledge is separate from qualification
            // of the admission primitive. A block device alone could be GFS2/OCFS2.
            var localFormats = new[] { "f2fs", "jfs", "nilfs2", "reiserfs", "vfat", "exfat", "ntfs3", "udf", "ufs", "minix", "bcachefs" };
            return localFormats.Contains(format) && major != 0 && Directory.Exists("/sys/dev/block/" + mount[2]);
        }

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
