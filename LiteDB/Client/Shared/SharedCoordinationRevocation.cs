using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Missing markers are the hot path. Inspect native errors without allocating a
    /// FileNotFoundException, and distinguish absence from access/IO failures (which
    /// File.Exists deliberately folds together). The latter always revoke admission.
    /// </summary>
    internal static class SharedCoordinationRevocation
    {
        internal static bool IsRevoked(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetFileAttributes(path) != uint.MaxValue || Marshal.GetLastWin32Error() != 2;
                // access(F_OK) tests existence, including through symlinks, without
                // requiring read permission on the marker itself. Only ENOENT is absent.
#if NET8_0_OR_GREATER
                return Access(path, 0) == 0 || Marshal.GetLastWin32Error() != 2;
#else
                return Access(Encoding.UTF8.GetBytes(path + "\0"), 0) == 0 || Marshal.GetLastWin32Error() != 2;
#endif
            }
            catch (DllNotFoundException) { return true; }
            catch (EntryPointNotFoundException) { return true; }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string path);

        [DllImport("libc", EntryPoint = "access", SetLastError = true)]
#if NET8_0_OR_GREATER
        private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
#else
        private static extern int Access([In] byte[] path, int mode);
#endif
    }
}
