using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileLock_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(DatabaseFileIdentity.CanonicalPath(Path.GetTempPath()), "litedb-one-lock-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test-shm");
        public SharedOneFileLock_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Dms_slot_write_and_cross_thread_release_use_independent_descriptions()
        {
            using var first = new SharedRangeLocks(Filename);
            first.TryExclusiveDms().Should().BeTrue();
            using var second = new SharedRangeLocks(Filename);
            second.TryExclusiveDms().Should().BeFalse();
            first.DowngradeDms();
            second.ShareDms();
            first.TryClaim(0).Should().BeTrue();
            first.IsLive(0).Should().BeTrue("own locks require a separately opened prober");
            second.TryClaim(0).Should().BeFalse();
            second.TryClaim(1).Should().BeTrue();
            first.EnterWrite();
            Action blocked = second.EnterWrite;
            blocked.Should().Throw<IOException>();
            using (var unrelated = new FileStream(Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            second.IsLive(0).Should().BeTrue();
            await Task.Run(first.Dispose);
            second.IsLive(0).Should().BeFalse();
            second.EnterWrite();
            second.ExitWrite();
            second.ReleaseSlot(1);
        }

        [Fact]
        public void Probe_errors_are_unknown_and_never_reported_as_dead()
        {
            var locks = new SharedRangeLocks(Filename);
            locks.Dispose();
            Action probe = () => locks.IsLive(0);
            probe.Should().Throw<Exception>();
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Stopped_process_stays_live_and_kill_releases_all_three_ranges()
        {
            using var child = new MvccProcess("one-file-lock-hold", Filename, null);
            await child.Expect("ready");
            using var observer = new SharedRangeLocks(Filename);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                kill(child.ProcessId, 19).Should().Be(0); // SIGSTOP
                try
                {
                    observer.IsLive(0).Should().BeTrue();
                    observer.TryExclusiveDms().Should().BeFalse();
                    Action write = observer.EnterWrite;
                    write.Should().Throw<IOException>();
                }
                finally { kill(child.ProcessId, 18).Should().Be(0); } // SIGCONT
            }
            await child.Kill(sharedFilesRemainOpen: true);
            observer.IsLive(0).Should().BeFalse();
            observer.TryExclusiveDms().Should().BeTrue();
            observer.EnterWrite();
        }
#endif

        [Fact]
        public void Linux_negative_control_POSIX_close_drops_lock_but_OFD_close_and_duplicate_do_not()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            using var owner = DatabaseFileIdentity.Open(Filename, false, true);
            using var peer = DatabaseFileIdentity.Open(Filename, false, false);
            // F_GETLK via another process is unnecessary: an OFD query conflicts
            // with POSIX locks even in this process (unlike a POSIX GETLK query).
            var posix = new DatabaseFileLock.LinuxFlock { Type = 1, Start = SharedMemoryLayout.Write, Length = 1 };
            DatabaseUnixNative.Api.LockLinux(owner, 6, ref posix).Should().Be(0); // F_SETLK
            using var probe = new DatabaseFileLock(Filename, false, false);
            probe.Conflicts(SharedMemoryLayout.Write).Should().BeTrue();
            DatabaseFileIdentity.Open(Filename, false, false).Dispose();
            probe.Conflicts(SharedMemoryLayout.Write).Should().BeFalse();
            var ofd = posix;
            DatabaseUnixNative.Api.LockLinux(owner, 37, ref ofd).Should().Be(0);
            DatabaseFileIdentity.Open(Filename, false, false).Dispose();
            probe.Conflicts(SharedMemoryLayout.Write).Should().BeTrue();
            using var duplicate = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)dup(owner.DangerousGetHandle().ToInt32()), true);
            var ownQuery = new DatabaseFileLock.LinuxFlock { Type = 1, Start = SharedMemoryLayout.Write, Length = 1 };
            DatabaseUnixNative.Api.LockLinux(duplicate, 36, ref ownQuery).Should().Be(0);
            ownQuery.Type.Should().Be(2, "duplicated OFD descriptions cannot probe their own locks");
            duplicate.Dispose();
            probe.Conflicts(SharedMemoryLayout.Write).Should().BeTrue();
        }

        [DllImport("libc", SetLastError = true)] private static extern int dup(int descriptor);
        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
