#if NET8_0_OR_GREATER
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiteDB.Tests
{
    // Preserve the same process-freeze and hard-link scenarios on Windows.
    // NtSuspendProcess freezes all existing threads together, including renewal
    // timers; handles are scoped here and are never inherited by child probes.
    internal static class MappedPlatformTestNative
    {
        internal static int Signal(int processId, int signal)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return kill(processId, signal);
            using var process = Process.GetProcessById(processId);
            if (signal == 19) return NtSuspendProcess(process.Handle);
            if (signal == 18) return NtResumeProcess(process.Handle);
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        internal static int Link(string original, string alias)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return link(original, alias);
            return CreateHardLink(alias, original, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
        }

        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
        [DllImport("libc", SetLastError = true)] private static extern int link(string original, string alias);
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string alias, string original, IntPtr security);
    }
}
#endif
