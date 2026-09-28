using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static LiteDB.Client.Shared.DatabaseFileLock;

namespace LiteDB.Client.Shared
{
    internal static class DatabaseUnixNative
    {
        internal delegate int OpenCall(string path, int flags, int mode);
        internal delegate int StatCall(SafeFileHandle handle, byte[] data);
        internal delegate int LegacyStatCall(int version, SafeFileHandle handle, byte[] data);
        internal delegate IntPtr RealPathCall(string path, IntPtr result);
        internal delegate IntPtr ReadLinkCall(string path, out byte value, UIntPtr size);
        internal delegate int LinuxLockCall(SafeFileHandle handle, int command, ref LinuxFlock value);
        internal delegate int DarwinLockCall(SafeFileHandle handle, int command, ref DarwinFlock value);
        private delegate int ArmOpenCall(string path, int flags,
            long x2, long x3, long x4, long x5, long x6, long x7, int mode);
        private delegate int ArmLockCall(SafeFileHandle handle, int command,
            long x2, long x3, long x4, long x5, long x6, long x7, ref DarwinFlock value);

        internal sealed class Binding
        {
            internal OpenCall Open;
            internal StatCall Stat, FileSystemStat;
            internal LegacyStatCall LegacyStat;
            internal RealPathCall RealPath;
            internal ReadLinkCall ReadLink;
            internal Action<IntPtr> Free;
            internal LinuxLockCall LockLinux;
            internal DarwinLockCall LockDarwin;
        }

        internal static readonly Binding Api = Resolve();
        private static volatile bool _legacyStat;

        internal static int Stat(SafeFileHandle handle, byte[] data)
        {
            if (_legacyStat) return LegacyStat(handle, data);
            try { return Api.Stat(handle, data); }
            catch (EntryPointNotFoundException) when (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // glibc before 2.33 exposes __fxstat instead of fstat. Its version
                // selects the same 64-bit layout used by DatabaseFileIdentity.
                _legacyStat = true;
                return LegacyStat(handle, data);
            }
        }

        private static int LegacyStat(SafeFileHandle handle, byte[] data) =>
            Api.LegacyStat(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0 : 1, handle, data);

        private static Binding Resolve()
        {
            // Reuse the soname selected by the existing native-sync probe, rather
            // than assuming every netstandard host recognizes the libc alias.
            Binding binding;
            switch (NativeLibc.LibraryName)
            {
                case "libc":
                    binding = new Binding
                    {
                        Open = DatabaseUnixBindings.Generic.Open, Stat = DatabaseUnixBindings.Generic.Stat,
                        FileSystemStat = DatabaseUnixBindings.Generic.FileSystemStat,
                        RealPath = DatabaseUnixBindings.Generic.RealPath, ReadLink = DatabaseUnixBindings.Generic.ReadLink,
                        Free = DatabaseUnixBindings.Generic.Free,
                        LegacyStat = DatabaseUnixBindings.Generic.LegacyStat, LockLinux = DatabaseUnixBindings.Generic.LockLinux,
                    };
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        ConfigureDarwin(binding, DatabaseUnixBindings.Generic.StatDarwinX64,
                            DatabaseUnixBindings.Generic.FileSystemStatDarwinX64, DatabaseUnixBindings.Generic.OpenDarwinArm64,
                            DatabaseUnixBindings.Generic.LockDarwin, DatabaseUnixBindings.Generic.LockDarwinArm64);
                    return binding;
                case "libc.so.6":
                    binding = new Binding
                    {
                        Open = DatabaseUnixBindings.Glibc.Open, Stat = DatabaseUnixBindings.Glibc.Stat,
                        FileSystemStat = DatabaseUnixBindings.Glibc.FileSystemStat,
                        RealPath = DatabaseUnixBindings.Glibc.RealPath, ReadLink = DatabaseUnixBindings.Glibc.ReadLink,
                        Free = DatabaseUnixBindings.Glibc.Free,
                        LegacyStat = DatabaseUnixBindings.Glibc.LegacyStat, LockLinux = DatabaseUnixBindings.Glibc.LockLinux,
                    };
                    return binding;
                case "/usr/lib/libSystem.B.dylib":
                    binding = new Binding
                    {
                        Open = DatabaseUnixBindings.System.Open, Stat = DatabaseUnixBindings.System.Stat,
                        FileSystemStat = DatabaseUnixBindings.System.FileSystemStat,
                        RealPath = DatabaseUnixBindings.System.RealPath, ReadLink = DatabaseUnixBindings.System.ReadLink,
                        Free = DatabaseUnixBindings.System.Free,
                    };
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        ConfigureDarwin(binding, DatabaseUnixBindings.System.StatDarwinX64,
                            DatabaseUnixBindings.System.FileSystemStatDarwinX64, DatabaseUnixBindings.System.OpenDarwinArm64,
                            DatabaseUnixBindings.System.LockDarwin, DatabaseUnixBindings.System.LockDarwinArm64);
                    return binding;
                case "libc.musl-x86_64.so.1":
                    binding = new Binding
                    {
                        Open = DatabaseUnixBindings.MuslX64.Open, Stat = DatabaseUnixBindings.MuslX64.Stat,
                        FileSystemStat = DatabaseUnixBindings.MuslX64.FileSystemStat,
                        RealPath = DatabaseUnixBindings.MuslX64.RealPath, ReadLink = DatabaseUnixBindings.MuslX64.ReadLink,
                        Free = DatabaseUnixBindings.MuslX64.Free,
                        LegacyStat = DatabaseUnixBindings.MuslX64.LegacyStat, LockLinux = DatabaseUnixBindings.MuslX64.LockLinux,
                    };
                    return binding;
                case "libc.musl-aarch64.so.1":
                    binding = new Binding
                    {
                        Open = DatabaseUnixBindings.MuslArm64.Open, Stat = DatabaseUnixBindings.MuslArm64.Stat,
                        FileSystemStat = DatabaseUnixBindings.MuslArm64.FileSystemStat,
                        RealPath = DatabaseUnixBindings.MuslArm64.RealPath, ReadLink = DatabaseUnixBindings.MuslArm64.ReadLink,
                        Free = DatabaseUnixBindings.MuslArm64.Free,
                        LegacyStat = DatabaseUnixBindings.MuslArm64.LegacyStat, LockLinux = DatabaseUnixBindings.MuslArm64.LockLinux,
                    };
                    return binding;
                default:
                    throw new PlatformNotSupportedException("No supported native C library is available for database admission.");
            }
        }

        private static void ConfigureDarwin(Binding binding, StatCall statX64, StatCall fileSystemStatX64,
            ArmOpenCall openArm64, DarwinLockCall lockX64, ArmLockCall lockArm64)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                binding.Stat = statX64;
                binding.FileSystemStat = fileSystemStatX64;
                binding.LockDarwin = lockX64;
            }
            else
            {
                // Apple's arm64 variadic ABI puts the final argument on the stack.
                // The padding deliberately occupies the remaining argument registers.
                binding.Open = (path, flags, mode) => openArm64(path, flags, 0, 0, 0, 0, 0, 0, mode);
                binding.LockDarwin = (SafeFileHandle handle, int command, ref DarwinFlock value) =>
                    lockArm64(handle, command, 0, 0, 0, 0, 0, 0, ref value);
            }
        }
    }
}

