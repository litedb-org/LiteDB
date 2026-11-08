#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    internal static class SharedProcessSignals
    {
        internal static void Stop(int process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // NtSuspendProcess synchronously freezes every existing thread.
                MappedPlatformTestNative.Signal(process, 19).Should().Be(0, "native suspension must freeze the child renewal timer");
                return;
            }
            var signal = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 17 : 19;
            kill(process, signal).Should().Be(0, "SIGSTOP must freeze the child renewal timer");
            // A successful kill only queued the signal. Observe WIFSTOPPED before
            // asserting lease expiry or liveness, rather than racing signal delivery.
            SpinWait.SpinUntil(() =>
            {
                var result = waitpid(process, out var status, 3); // WNOHANG | WUNTRACED
                return result == process && (status & 0xff) == 0x7f;
            }, TimeSpan.FromSeconds(5)).Should().BeTrue("the OS must report the child stopped");
        }

        internal static void Resume(int process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MappedPlatformTestNative.Signal(process, 18).Should().Be(0, "native resume must release the stopped child");
                return;
            }
            var signal = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 19 : 18;
            kill(process, signal).Should().Be(0, "SIGCONT must resume the stopped child");
        }

        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
        [DllImport("libc", SetLastError = true)] private static extern int waitpid(int process, out int status, int options);
    }
}
#endif
