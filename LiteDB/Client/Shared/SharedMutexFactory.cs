using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LiteDB
{
    internal static class SharedMutexFactory
    {
        private const string MutexPrefix = "Global\\";
        private const string MutexSuffix = ".Mutex";

        public static Mutex Create(string name)
        {
            var fullName = MutexPrefix + name + MutexSuffix;

            if (!IsWindows())
            {
                return new Mutex(false, fullName);
            }

            try
            {
                return WindowsMutex.Create(fullName);
            }
            catch (Win32Exception ex)
            {
                throw new PlatformNotSupportedException("Shared mode is not supported because named mutex access control is unavailable on this platform.", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException("Shared mode is not supported because named mutex access control is unavailable on this platform.", ex);
            }
            catch (DllNotFoundException ex)
            {
                throw new PlatformNotSupportedException("Shared mode is not supported because named mutex access control is unavailable on this platform.", ex);
            }
        }

#if NET6_0_OR_GREATER
        private static bool IsWindows()
        {
            return OperatingSystem.IsWindows();
        }
#else
        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
#endif

        private static class WindowsMutex
        {
            private const string WorldAccessSecurityDescriptor = "D:(A;;GA;;;WD)";
            private const uint SddlRevision1 = 1;

            public static Mutex Create(string name)
            {
                IntPtr descriptor = IntPtr.Zero;

                try
                {
                    if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(WorldAccessSecurityDescriptor, SddlRevision1, out descriptor, out _))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create security descriptor for shared mutex.");
                    }

                    var attributes = new NativeMethods.SECURITY_ATTRIBUTES
                    {
                        nLength = (uint)Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
                        bInheritHandle = 0,
                        lpSecurityDescriptor = descriptor
                    };

                    var handle = NativeMethods.CreateMutexEx(ref attributes, name, 0, NativeMethods.MUTEX_ALL_ACCESS);

                    if (handle == IntPtr.Zero || handle == NativeMethods.InvalidHandleValue)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create shared mutex with global access.");
                    }

                    var mutex = new Mutex();
                    mutex.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);

                    return mutex;
                }
                finally
                {
                    if (descriptor != IntPtr.Zero)
                    {
                        NativeMethods.LocalFree(descriptor);
                    }
                }
            }
        }

        private static class NativeMethods
        {
            public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
            public const uint MUTEX_ALL_ACCESS = 0x001F0001;

            [StructLayout(LayoutKind.Sequential)]
            public struct SECURITY_ATTRIBUTES
            {
                public uint nLength;
                public IntPtr lpSecurityDescriptor;
                public int bInheritHandle;
            }

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
                string StringSecurityDescriptor,
                uint StringSDRevision,
                out IntPtr SecurityDescriptor,
                out uint SecurityDescriptorSize);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateMutexEx(
                ref SECURITY_ATTRIBUTES lpMutexAttributes,
                string lpName,
                uint dwFlags,
                uint dwDesiredAccess);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr LocalFree(IntPtr hMem);
        }
    }
}
