using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB.Client.Shared
{
    // Compare live mount IDs instead of reparsing the entire namespace on each open.
    // No cache: bind mounts and mount namespaces can change while a process runs.
    internal static class DatabaseMountIdentity
    {
        private const uint MountId = 0x1000;

        internal static bool? IsFileMount(string filename)
        {
            try
            {
                var call = DatabaseUnixNative.Api.MountIdentity;
                if (call == null) return null;
                // AT_FDCWD, normal pathname resolution; ENOENT is a new database.
                var result = call(-100, filename, 0, MountId, out var file);
                if (result != 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 2) return false;
                    if (error == 38 || error == 22 || error == 95) return null;
                    throw DatabaseFileLock.Error("statx database mount");
                }
                if ((file.Mask & MountId) == 0) return null;
                if (call(-100, Path.GetDirectoryName(filename), 0, MountId, out var parent) != 0)
                    throw DatabaseFileLock.Error("statx parent mount");
                if ((parent.Mask & MountId) == 0) return null;
                return file.Mount != parent.Mount;
            }
            catch (EntryPointNotFoundException) { return null; }
        }

        // Linux's fixed statx ABI, identical on x64/arm64. The complete 256-byte
        // result is reserved so the kernel never overwrites the managed value.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct Statx
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(144)] internal ulong Mount;
        }
    }
}
