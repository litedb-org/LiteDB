using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Handle-owned locks outside the database address space. OFD locks deliberately
    /// do not use POSIX process locks (closing ANY descriptor would release those),
    /// or flock (used independently by the runtime for FileStream sharing).
    /// </summary>
    internal sealed class DatabaseFileLock : IDisposable
    {
        internal const long Admission = long.MaxValue - 4096;
        internal const long Family = Admission + 1;
        private readonly SafeFileHandle _handle;
        internal string Identity { get; }
        internal string Path { get; }
        internal bool ReadOnly { get; }

        internal DatabaseFileLock(string filename, bool readOnly, bool create)
        {
            _handle = DatabaseFileIdentity.Open(filename, readOnly, create);
            ReadOnly = readOnly;
            try
            {
                Identity = DatabaseFileIdentity.Read(_handle);
                Path = DatabaseFileIdentity.CanonicalPath(filename);
                DatabaseFileIdentity.RequireLocalVolume(_handle, Path);
            }
            catch { _handle.Dispose(); throw; }
        }

        internal void Lock(long offset, bool exclusive)
        {
            SharedCoordinationFile.Observe(this.Path, exclusive ? "mode-exclusive-lock" : "mode-shared-lock");
            if (DatabaseFileIdentity.Windows)
            {
                var position = Position(offset);
                if (!LockFileEx(_handle, exclusive ? 3u : 1u, 0, 1, 0, ref position))
                    throw Error("LockFileEx");
            }
            else this.UnixLock(offset, exclusive ? WriteLock : ReadLock, query: false);
        }

        internal void Unlock(long offset)
        {
            if (DatabaseFileIdentity.Windows)
            {
                var position = Position(offset);
                if (!UnlockFileEx(_handle, 0, 1, 0, ref position)) throw Error("UnlockFileEx");
            }
            else this.UnixLock(offset, UnlockLock, query: false);
        }

        internal void Downgrade(long offset)
        {
            // OFD conversion is atomic. Windows permits a shared lock over this
            // handle's exclusive lock; its first unlock then removes the exclusive
            // layer, leaving the shared lock continuously held.
            this.Lock(offset, exclusive: false);
            if (DatabaseFileIdentity.Windows) this.Unlock(offset);
        }

        // Only used for ranges this handle does not own, under the admission mutex.
        // F_OFD_GETLK works on read-only descriptors, unlike a trial write lock.
        internal bool Conflicts(long offset)
        {
            if (!DatabaseFileIdentity.Windows)
                return this.UnixLock(offset, WriteLock, query: true) != UnlockLock;
            var position = Position(offset);
            if (!LockFileEx(_handle, 3, 0, 1, 0, ref position))
            {
                if (Marshal.GetLastWin32Error() == 33) return true;
                throw Error("LockFileEx probe");
            }
            this.Unlock(offset);
            return false;
        }

        private static bool Darwin => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static short ReadLock => Darwin ? (short)1 : (short)0;
        private static short WriteLock => Darwin ? (short)3 : (short)1;
        private static short UnlockLock => 2;

        private short UnixLock(long offset, short type, bool query)
        {
            int result;
            short actual;
            do
            {
                if (Darwin)
                {
                    var value = new DarwinFlock { Start = offset, Length = 1, Type = type };
                    result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                        ? DarwinArm64Fcntl(_handle, query ? 92 : 90, 0, 0, 0, 0, 0, 0, ref value)
                        : DarwinFcntl(_handle, query ? 92 : 90, ref value);
                    actual = value.Type;
                }
                else
                {
                    var value = new LinuxFlock { Start = offset, Length = 1, Type = type };
                    result = LinuxFcntl(_handle, query ? 36 : 37, ref value);
                    actual = value.Type;
                }
            } while (result < 0 && Marshal.GetLastWin32Error() == 4); // EINTR
            if (result < 0) throw Error("fcntl OFD lock");
            return actual;
        }

        internal static IOException Error(string operation)
        {
            var code = Marshal.GetLastWin32Error();
            return new IOException(operation + " failed (native error " + code + "). " +
                "Database admission requires working OS locks on a supported local filesystem.",
                DatabaseFileIdentity.Windows ? unchecked((int)0x80070000) | code : unchecked((int)0x80131620));
        }

        public void Dispose() => _handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxFlock
        {
            internal short Type, Whence;
            internal long Start, Length;
            internal int Pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DarwinFlock
        {
            internal long Start, Length;
            internal int Pid;
            internal short Type, Whence;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Overlapped
        {
            internal IntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }

        private static Overlapped Position(long offset) => new Overlapped
            { Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32) };

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int LinuxFcntl(SafeFileHandle handle, int command, ref LinuxFlock value);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int DarwinFcntl(SafeFileHandle handle, int command, ref DarwinFlock value);
        // Apple's arm64 variadic ABI places the third argument on the stack,
        // unlike its fixed-argument ABI. x2..x7 intentionally occupy registers.
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int DarwinArm64Fcntl(SafeFileHandle handle, int command,
            long x2, long x3, long x4, long x5, long x6, long x7, ref DarwinFlock value);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved,
            uint length, uint lengthHigh, ref Overlapped position);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnlockFileEx(SafeFileHandle handle, uint reserved,
            uint length, uint lengthHigh, ref Overlapped position);
    }
}
