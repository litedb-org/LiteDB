using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static LiteDB.Client.Shared.DatabaseFileLock;

namespace LiteDB.Client.Shared
{
    // Explicit sonames also work on netstandard2.0 hosts without NativeLibrary.
    internal static class DatabaseUnixBindings
    {
        internal static class Generic
        {
            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags, int mode);
            [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
            internal static extern int Stat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
            internal static extern int FileSystemStat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
            internal static extern IntPtr RealPath(string path, IntPtr result);
            [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
            internal static extern IntPtr ReadLink(string path, out byte value, UIntPtr size);
            [DllImport("libc", EntryPoint = "free", SetLastError = true)]
            internal static extern void Free(IntPtr pointer);
            [DllImport("libc", EntryPoint = "__fxstat", SetLastError = true)]
            internal static extern int LegacyStat(int version, SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockLinux(SafeFileHandle handle, int command, ref LinuxFlock value);
            [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
            internal static extern int StatDarwinX64(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc", EntryPoint = "fstatfs$INODE64", SetLastError = true)]
            internal static extern int FileSystemStatDarwinX64(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            internal static extern int OpenDarwinArm64(string path, int flags,
                long x2, long x3, long x4, long x5, long x6, long x7, int mode);
            [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockDarwin(SafeFileHandle handle, int command, ref DarwinFlock value);
            [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockDarwinArm64(SafeFileHandle handle, int command,
                long x2, long x3, long x4, long x5, long x6, long x7, ref DarwinFlock value);
        }

        internal static class Glibc
        {
            [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags, int mode);
            [DllImport("libc.so.6", EntryPoint = "fstat", SetLastError = true)]
            internal static extern int Stat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.so.6", EntryPoint = "fstatfs", SetLastError = true)]
            internal static extern int FileSystemStat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.so.6", EntryPoint = "realpath", SetLastError = true)]
            internal static extern IntPtr RealPath(string path, IntPtr result);
            [DllImport("libc.so.6", EntryPoint = "readlink", SetLastError = true)]
            internal static extern IntPtr ReadLink(string path, out byte value, UIntPtr size);
            [DllImport("libc.so.6", EntryPoint = "free", SetLastError = true)]
            internal static extern void Free(IntPtr pointer);
            [DllImport("libc.so.6", EntryPoint = "__fxstat", SetLastError = true)]
            internal static extern int LegacyStat(int version, SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.so.6", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockLinux(SafeFileHandle handle, int command, ref LinuxFlock value);
        }

        internal static class System
        {
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags, int mode);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
            internal static extern int Stat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstatfs", SetLastError = true)]
            internal static extern int FileSystemStat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
            internal static extern IntPtr RealPath(string path, IntPtr result);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "readlink", SetLastError = true)]
            internal static extern IntPtr ReadLink(string path, out byte value, UIntPtr size);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free", SetLastError = true)]
            internal static extern void Free(IntPtr pointer);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
            internal static extern int StatDarwinX64(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstatfs$INODE64", SetLastError = true)]
            internal static extern int FileSystemStatDarwinX64(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
            internal static extern int OpenDarwinArm64(string path, int flags,
                long x2, long x3, long x4, long x5, long x6, long x7, int mode);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockDarwin(SafeFileHandle handle, int command, ref DarwinFlock value);
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockDarwinArm64(SafeFileHandle handle, int command,
                long x2, long x3, long x4, long x5, long x6, long x7, ref DarwinFlock value);
        }

        internal static class MuslX64
        {
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags, int mode);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "fstat", SetLastError = true)]
            internal static extern int Stat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "fstatfs", SetLastError = true)]
            internal static extern int FileSystemStat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "realpath", SetLastError = true)]
            internal static extern IntPtr RealPath(string path, IntPtr result);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "readlink", SetLastError = true)]
            internal static extern IntPtr ReadLink(string path, out byte value, UIntPtr size);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "free", SetLastError = true)]
            internal static extern void Free(IntPtr pointer);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "__fxstat", SetLastError = true)]
            internal static extern int LegacyStat(int version, SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-x86_64.so.1", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockLinux(SafeFileHandle handle, int command, ref LinuxFlock value);
        }

        internal static class MuslArm64
        {
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "open", SetLastError = true)]
            internal static extern int Open(string path, int flags, int mode);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "fstat", SetLastError = true)]
            internal static extern int Stat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "fstatfs", SetLastError = true)]
            internal static extern int FileSystemStat(SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "realpath", SetLastError = true)]
            internal static extern IntPtr RealPath(string path, IntPtr result);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "readlink", SetLastError = true)]
            internal static extern IntPtr ReadLink(string path, out byte value, UIntPtr size);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "free", SetLastError = true)]
            internal static extern void Free(IntPtr pointer);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "__fxstat", SetLastError = true)]
            internal static extern int LegacyStat(int version, SafeFileHandle handle, [Out] byte[] data);
            [DllImport("libc.musl-aarch64.so.1", EntryPoint = "fcntl", SetLastError = true)]
            internal static extern int LockLinux(SafeFileHandle handle, int command, ref LinuxFlock value);
        }

    }
}

