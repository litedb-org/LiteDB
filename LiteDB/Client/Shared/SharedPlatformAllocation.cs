#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Client.Shared
{
    // The mapped authority is a real file.  Reserve its extents before publishing
    // either envelope so a successful bootstrap never depends on a later lazy
    // allocation.  Each supported OS has a separate native implementation; in
    // particular, Windows must never resolve a Unix libc entry point.
    internal static class SharedPlatformAllocation
    {
        private const int F_PREALLOCATE = 42;
        private const int F_ALLOCATECONTIG = 0x00000002;
        private const int F_ALLOCATEALL = 0x00000004;
        private const int F_PEOFPOSMODE = 3;
        private const int FileAllocationInfoClass = 5;
        private const int ErrorInvalidFunction = 1;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorNotSupported = 50;

        internal static int Allocate(FileStream file, long length)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return AllocateWindows(file, length);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return AllocateDarwin(file, length);
            return posix_fallocate(file.SafeFileHandle, 0, length);
        }

        private static int AllocateWindows(FileStream file, long length)
        {
            var allocation = new FileAllocationInfo { AllocationSize = length };
            if (SetFileInformationByHandle(file.SafeFileHandle, FileAllocationInfoClass,
                ref allocation, (uint)Marshal.SizeOf<FileAllocationInfo>()))
                return 0;

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInvalidFunction && error != ErrorInvalidParameter && error != ErrorNotSupported)
                return error;

            // Some Windows filesystems do not implement FileAllocationInfo.  The
            // already-published logical length is retained and a tail write gives
            // the filesystem a compatible allocation path without a Unix import.
            var position = file.Position;
            try
            {
                file.Position = length - 1;
                file.WriteByte(0);
                file.Flush(true);
                return 0;
            }
            finally { file.Position = position; }
        }

        private static int AllocateDarwin(FileStream file, long length)
        {
            var store = new DarwinFStore
            {
                Flags = F_ALLOCATECONTIG,
                PositionMode = F_PEOFPOSMODE,
                Offset = 0,
                Length = length,
                BytesAllocated = 0
            };
            var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? fcntl_arm64(file.SafeFileHandle, F_PREALLOCATE, 0, 0, 0, 0, 0, 0, ref store)
                : fcntl(file.SafeFileHandle, F_PREALLOCATE, ref store);
            if (result < 0)
            {
                store.Flags = F_ALLOCATEALL;
                result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? fcntl_arm64(file.SafeFileHandle, F_PREALLOCATE, 0, 0, 0, 0, 0, 0, ref store)
                    : fcntl(file.SafeFileHandle, F_PREALLOCATE, ref store);
            }
            if (result < 0) return Marshal.GetLastWin32Error();
            if (ftruncate(file.SafeFileHandle, length) != 0) return Marshal.GetLastWin32Error();
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DarwinFStore
        {
            internal uint Flags;
            internal int PositionMode;
            internal long Offset;
            internal long Length;
            internal long BytesAllocated;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAllocationInfo
        {
            internal long AllocationSize;
        }

        [DllImport("libc", EntryPoint = "posix_fallocate", SetLastError = true)]
        private static extern int posix_fallocate(SafeFileHandle file, long offset, long length);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int fcntl(SafeFileHandle file, int command, ref DarwinFStore store);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int fcntl_arm64(SafeFileHandle file, int command,
            long x2, long x3, long x4, long x5, long x6, long x7, ref DarwinFStore store);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "ftruncate", SetLastError = true)]
        private static extern int ftruncate(SafeFileHandle file, long length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
            ref FileAllocationInfo fileInformation, uint bufferSize);
    }
}
#endif
