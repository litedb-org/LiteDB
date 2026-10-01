using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiteDB.Client.Shared
{
    internal static class TransactionHolderContext
    {
        internal static void Validate()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            // The idle holder deliberately does not inherit application ExecutionContext.
            // Never silently open files as the process identity for an impersonated caller.
            if (OpenThreadToken(GetCurrentThread(), 0x0008 /* TOKEN_QUERY */, true, out var token))
            {
                CloseHandle(token);
                throw new NotSupportedException("Shared transaction handles cannot begin under Windows thread impersonation.");
            }
            var error = Marshal.GetLastWin32Error();
            if (error != 1008 /* ERROR_NO_TOKEN */)
                throw new Win32Exception(error, "Cannot establish the Shared transaction caller's Windows identity.");
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenThreadToken(IntPtr thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
