#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public sealed class UnixTheoryAttribute : TheoryAttribute
    {
        public UnixTheoryAttribute()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Skip = "Unix syscall error injection; Windows uses LockFileEx";
        }
    }

    public class SharedRequestAdmissionErrors_Tests
    {
        [UnixTheory]
        [InlineData(4)] // EINTR
        [InlineData(13)] // EACCES
        [InlineData(-1)] // platform EAGAIN / EWOULDBLOCK
        [InlineData(22)] // EINVAL must propagate, never masquerade as contention
        public void Native_errors_preserve_deadline_refusal_and_real_lock_retry(int error)
        {
            using var file = new MappedTestFile();
            using var contender = new SharedRangeLocks(file.Filename);
            var darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            if (error == -1) error = darwin ? 35 : 11;
            var thread = Thread.CurrentThread;
            var linux = DatabaseUnixNative.Api.LockLinux;
            var apple = DatabaseUnixNative.Api.LockDarwin;
            var calls = 0;
            try
            {
                // Other concurrently executing tests retain the real syscall.
                DatabaseUnixNative.Api.LockLinux = (Microsoft.Win32.SafeHandles.SafeFileHandle handle, int command,
                    ref DatabaseFileLock.LinuxFlock value) =>
                {
                    if (Thread.CurrentThread != thread) return linux(handle, command, ref value);
                    calls++;
                    Marshal.SetLastPInvokeError(error);
                    return -1;
                };
                DatabaseUnixNative.Api.LockDarwin = (Microsoft.Win32.SafeHandles.SafeFileHandle handle, int command,
                    ref DatabaseFileLock.DarwinFlock value) =>
                {
                    if (Thread.CurrentThread != thread) return apple(handle, command, ref value);
                    calls++;
                    Marshal.SetLastPInvokeError(error);
                    return -1;
                };
                Action acquire = () => contender.ShareDms(TimeSpan.FromMilliseconds(30));
                if (error == 22) acquire.Should().Throw<System.IO.IOException>().WithMessage("*native error 22*");
                else
                {
                    acquire.Should().Throw<TimeoutException>();
                    calls.Should().BeGreaterThan(1, "interruption and contention must retry until the deadline");
                }
            }
            finally
            {
                DatabaseUnixNative.Api.LockLinux = linux;
                DatabaseUnixNative.Api.LockDarwin = apple;
            }
            using (var probe = new SharedRangeLocks(file.Filename))
                probe.TryExclusiveDms().Should().BeTrue("failed admission must not own DMS");
            contender.ShareDms(TimeSpan.FromSeconds(1));
            using var blocked = new SharedRangeLocks(file.Filename);
            blocked.TryExclusiveDms().Should().BeFalse("a retry succeeds only by taking the real OS lock");
        }
    }
}
#endif
