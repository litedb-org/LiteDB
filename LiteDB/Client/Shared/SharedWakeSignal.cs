#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // A wake is only a hint; every waiter rechecks the mapped sequence and
    // predicate. The name is tied to the authority generation so a recreated
    // -shm file cannot consume a predecessor's signal.
    internal sealed class SharedWakeSignal : IDisposable
    {
        private readonly EventWaitHandle _windows;
        private readonly IntPtr _darwin;
        private readonly bool _isDarwin;
#if DEBUG || TESTING
        internal Action<string> NativeWakeStage;
#endif

        private SharedWakeSignal(Guid authority)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _windows = new EventWaitHandle(false, EventResetMode.AutoReset,
                    "LiteDB.Shared.Wake." + authority.ToString("N"));
                return;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _isDarwin = true;
                var name = DarwinName(authority);
                // Darwin O_CREAT is 0x0200 (0x40 is O_ASYNC). On Apple
                // arm64 the variadic mode/value arguments must be on the stack.
                _darwin = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? sem_open_arm64(name, 0x0200, 0, 0, 0, 0, 0, 0, 0x1b6, 0)
                    : sem_open(name, 0x0200, 0x1b6, 0);
                if (_darwin == new IntPtr(-1))
                    throw new IOException("Opening the Shared macOS wake semaphore failed (native error " + Marshal.GetLastWin32Error() + ").");
                return;
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                throw new PlatformNotSupportedException("Shared writer wake signals require Windows, macOS or Linux.");
        }

        internal static SharedWakeSignal Open(Guid authority) =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? null : new SharedWakeSignal(authority);

        // Keep all 128 authority bits within Darwin's 31-byte semaphore name limit.
        internal static string DarwinName(Guid authority) => "/ldb-w-" +
            Convert.ToBase64String(authority.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static void Retire(Guid authority)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                sem_unlink(DarwinName(authority));
        }

        internal void Wake()
        {
            if (_windows != null)
            {
                _windows.Set();
                return;
            }
            if (_isDarwin && sem_post(_darwin) != 0)
                throw new IOException("Posting the Shared macOS wake semaphore failed (native error " + Marshal.GetLastWin32Error() + ").");
        }

        internal void Wait(int milliseconds)
        {
            if (_windows != null)
            {
                _windows.WaitOne(milliseconds);
                return;
            }
            if (!_isDarwin) return;

            // Darwin does not provide a usable sem_timedwait on all supported
            // baselines. A holder-local timer posts the same semaphore, which
            // gives sem_wait a bounded wake without depending on that API.
            var timer = new Timer(PostTimer, this, milliseconds, Timeout.Infinite);
            try
            {
                while (true)
                {
                    if (sem_wait(_darwin) == 0)
                    {
                        Observe("native-returned");
                        return;
                    }
                    var error = Marshal.GetLastWin32Error();
                    if (error == 4) continue;
                    throw new IOException("Waiting on the Shared macOS wake semaphore failed (native error " + error + ").");
                }
            }
            finally
            {
                // Dispose alone permits an already queued post to run after
                // the holder returns and closes the native semaphore handle.
                using var drained = new ManualResetEvent(false);
                if (timer.Dispose(drained)) drained.WaitOne();
            }
        }

        private static void PostTimer(object state)
        {
            var wake = (SharedWakeSignal)state;
            wake.Observe("timer-post");
            sem_post(wake._darwin);
        }

        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        private void Observe(string stage)
        {
#if DEBUG || TESTING
            NativeWakeStage?.Invoke(stage);
#endif
        }

        public void Dispose()
        {
            _windows?.Dispose();
            if (_isDarwin && _darwin != IntPtr.Zero && _darwin != new IntPtr(-1)) sem_close(_darwin);
        }

        [DllImport("libSystem.B.dylib", EntryPoint = "sem_open", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr sem_open(string name, int oflag, int mode, uint value);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_open", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr sem_open_arm64(string name, int oflag,
            long x2, long x3, long x4, long x5, long x6, long x7, long mode, long value);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_post", SetLastError = true)]
        private static extern int sem_post(IntPtr semaphore);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_wait", SetLastError = true)]
        private static extern int sem_wait(IntPtr semaphore);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_close", SetLastError = true)]
        private static extern int sem_close(IntPtr semaphore);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_unlink", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int sem_unlink(string name);
    }
}
#endif
