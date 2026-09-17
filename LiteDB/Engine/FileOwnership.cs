using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Engine
{
    internal sealed partial class FileOwnership : IDisposable
    {
        private FileStream _stream;
#if DEBUG || TESTING
        internal static Action<string> SimulateBeforeLock;
#endif

        private FileOwnership(FileStream stream) => _stream = stream;

        internal static FileOwnership Acquire(EngineSettings settings, bool forceExclusive = false)
        {
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) ||
                settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;

            if (IsVirtualRuntime)
                throw new PlatformNotSupportedException("Filename databases require native file ownership locks. Use a caller-owned DataStream on virtual filesystem hosts.");

            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!windows && !IsLinux && !IsDarwin && !RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
                throw new PlatformNotSupportedException("Native file ownership is unavailable on this platform. Use a caller-owned DataStream.");
            var exclusive = !settings.ReadOnly || forceExclusive;
            FileStream stream;
            try
            {
                stream = OpenFile(settings.Filename,
                    settings.ReadOnly ? FileMode.Open : FileMode.OpenOrCreate,
                    exclusive && !windows ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
            }
            catch (IOException ex) when (settings.ReadOnly &&
                (ex is FileNotFoundException || ex is DirectoryNotFoundException))
            {
                throw new LiteException(LiteException.FILE_NOT_FOUND, ex,
                    "File '{0}' does not exist and cannot be created in read-only mode.", settings.Filename);
            }
            try
            {
#if DEBUG || TESTING
                SimulateBeforeLock?.Invoke(settings.Filename);
#endif
                // Lock an unused byte beyond EOF without extending the file. The
                // handle identifies the actual file, including hard-link aliases.
                const long position = long.MaxValue - 1;
                bool acquired;
                if (windows)
                {
                    var overlap = new Overlapped { Offset = (uint)(position & uint.MaxValue), OffsetHigh = (uint)(position >> 32) };
                    acquired = LockFileEx(stream.SafeFileHandle, exclusive ? 3u : 1u, 0, 1, 0, ref overlap);
                }
                else if (IsDarwin)
                {
                    var region = new DarwinLock { Start = position, Length = 1, Type = exclusive ? (short)3 : (short)1 };
                    acquired = DarwinFcntl(stream.SafeFileHandle, 90, ref region) == 0;
                }
                else if (IsLinux && !UseLegacyLinuxLocks)
                {
                    // OFD locks belong to this handle, not the process. Closing a
                    // pooled reader must not release the engine's ownership lock.
                    if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                    {
                        var region = new LinuxLock32 { Start = position, Length = 1, Type = exclusive ? (short)1 : (short)0 };
                        acquired = LinuxFcntl32(stream.SafeFileHandle, 37, ref region) == 0;
                    }
                    else
                    {
                        var region = new LinuxLock { Start = position, Length = 1, Type = exclusive ? (short)1 : (short)0 };
                        acquired = (IntPtr.Size == 4
                            ? LinuxFcntlAligned32(stream.SafeFileHandle, 37, ref region)
                            : LinuxFcntl(stream.SafeFileHandle, 37, ref region)) == 0;
                    }
                }
                else
                {
                    // BSD flock locks also belong to the open file description.
                    acquired = Flock(stream.SafeFileHandle, (exclusive ? 2 : 1) | 4) == 0;
                }
                if (!acquired)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 11 || error == 13 || error == 33 || error == 35)
                        throw new LiteException(0, "Database file ownership is already held by another engine. Use Shared connections for coordinated access.");
                    throw new IOException("Unable to acquire database file ownership.", new Win32Exception(error));
                }
                VerifyPath(stream, settings.Filename);
                return new FileOwnership(stream);
            }
            catch (IOException ex) when (settings.ReadOnly &&
                (ex is FileNotFoundException || ex is DirectoryNotFoundException))
            {
                stream.Dispose();
                throw new LiteException(LiteException.FILE_NOT_FOUND, ex,
                    "File '{0}' does not exist and cannot be created in read-only mode.", settings.Filename);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxLock
        {
            internal short Type, Whence;
            internal long Start, Length;
            internal int ProcessId;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LinuxLock32
        {
            internal short Type, Whence;
            internal long Start, Length;
            internal int ProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DarwinLock
        {
            internal long Start, Length;
            internal int ProcessId;
            internal short Type, Whence;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Overlapped
        {
            internal IntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int LinuxFcntl(SafeFileHandle handle, int command, ref LinuxLock region);
        [DllImport("libc", EntryPoint = "fcntl64", SetLastError = true)]
        private static extern int LinuxFcntl32(SafeFileHandle handle, int command, ref LinuxLock32 region);
        [DllImport("libc", EntryPoint = "fcntl64", SetLastError = true)]
        private static extern int LinuxFcntlAligned32(SafeFileHandle handle, int command, ref LinuxLock region);
        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int Flock(SafeFileHandle handle, int operation);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int DarwinFcntl(SafeFileHandle handle, int command, ref DarwinLock region);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved,
            uint lengthLow, uint lengthHigh, ref Overlapped overlapped);
    }
}
