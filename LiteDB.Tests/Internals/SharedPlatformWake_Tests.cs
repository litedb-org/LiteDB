#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedPlatformWake_Tests
    {
        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
        [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
        public void Darwin_name_fits_native_limit_without_losing_authority_bits(string value)
        {
            var authority = Guid.Parse(value);
            var name = SharedWakeSignal.DarwinName(authority);
            name.Length.Should().BeLessThan(31);
            name.Should().StartWith("/ldb-w-");
            var encoded = name.Substring(7);
            encoded.Should().NotContain("/").And.NotContain("=").And.NotContain("+");
            new Guid(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=="))
                .Should().Be(authority);
        }

        [Fact]
        public void Native_backend_opens_peer_handles_and_recreates_a_retired_authority()
        {
            var authority = Guid.NewGuid();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                SharedWakeSignal.Open(authority).Should().BeNull("Linux uses the mapped futex directly");
                return;
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                try
                {
                    using var first = SharedWakeSignal.Open(authority);
                    using var peer = SharedWakeSignal.Open(authority);
                    first.Should().NotBeNull();
                    peer.Should().NotBeNull();
                    AssertDarwinNameExists(authority, false);
                    first.Prepare();
                    AssertDarwinNameExists(authority, true);
                    VerifyWake(first, peer, authority);
                    first.Dispose();
                    first.Dispose();
                    VerifyWake(peer, peer, authority);
                }
                finally { SharedWakeSignal.Retire(authority); }
                AssertDarwinNameExists(authority, false);
            }
        }

        internal static void AssertDarwinNameExists(Guid authority, bool expected)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
            var handle = sem_open(SharedWakeSignal.DarwinName(authority), 0);
            var error = Marshal.GetLastWin32Error();
            var exists = handle != new IntPtr(-1);
            if (exists) sem_close(handle);
            exists.Should().Be(expected);
            if (!expected) error.Should().Be(2, "the name must be absent, not merely inaccessible");
        }

        internal static void AssertNativeNameExists(Guid authority, bool expected)
        {
            AssertDarwinNameExists(authority, expected);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var exists = EventWaitHandle.TryOpenExisting("LiteDB.Shared.Wake." + authority.ToString("N"), out var handle);
            handle?.Dispose();
            exists.Should().Be(expected);
        }

        [Fact]
        public void Mapped_rekey_drains_native_waiters_retires_the_old_name_and_accepts_new_peer_wakes()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                SharedWakeSignal.Open(Guid.NewGuid()).Should().BeNull();
                return;
            }
            var original = Guid.NewGuid();
            var replacement = Guid.NewGuid();
            var path = Path.Combine(SharedMappedDirectory.Root, "wake-rekey-" + original.ToString("N"));
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
            file.SetLength(SharedMemoryLayout.Blocks);
            try
            {
                using var map = new SharedMemoryMap(file, file.Length, original);
                map.PrepareWake();
                using var entered = new ManualResetEventSlim();
                using var allowWait = new ManualResetEventSlim();
                using var rekeyStarted = new ManualResetEventSlim();
                map.WaitObserved = (stage, _) =>
                {
                    if (stage != "syscall") return;
                    entered.Set();
                    allowWait.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                };
                var waiting = Task.Run(() => map.WaitWake(map.Load(SharedMemoryLayout.WriterWake), 20));
                Task changing = null;
                try
                {
                    entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    changing = Task.Run(() => { rekeyStarted.Set(); map.RekeyWake(original, replacement); });
                    rekeyStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    changing.Wait(TimeSpan.FromMilliseconds(50)).Should().BeFalse("the native waiter still owns its handle");
                    AssertNativeNameExists(original, true);
                    AssertNativeNameExists(replacement, false);
                }
                finally
                {
                    allowWait.Set();
                    waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    waiting.GetAwaiter().GetResult();
                    if (changing != null)
                    {
                        changing.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        changing.GetAwaiter().GetResult();
                    }
                }
                AssertNativeNameExists(original, false);
                AssertNativeNameExists(replacement, true);
                using var peer = SharedWakeSignal.Open(replacement);
                peer.Prepare();
                entered.Reset();
                map.WaitObserved = (stage, _) => { if (stage == "syscall") entered.Set(); };
                waiting = Task.Run(() => map.WaitWake(map.Load(SharedMemoryLayout.WriterWake), 5000));
                try
                {
                    entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    peer.Wake();
                    waiting.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue("a replacement peer must wake the live map before timeout");
                }
                finally
                {
                    map.Wake();
                    waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    waiting.GetAwaiter().GetResult();
                    map.WaitObserved = null;
                }
            }
            finally
            {
                SharedWakeSignal.Retire(original);
                SharedWakeSignal.Retire(replacement);
            }
        }

        [DllImport("libSystem.B.dylib", EntryPoint = "sem_open", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr sem_open(string name, int oflag);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_close", SetLastError = true)]
        private static extern int sem_close(IntPtr semaphore);
        [DllImport("libSystem.B.dylib", EntryPoint = "sem_trywait", SetLastError = true)]
        private static extern int sem_trywait(IntPtr semaphore);

        private static void VerifyWake(SharedWakeSignal first, SharedWakeSignal peer, Guid authority)
        {
            using var posting = new ManualResetEventSlim();
            using var allowPost = new ManualResetEventSlim();
            using var returned = new ManualResetEventSlim();
            var darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            if (darwin)
            {
                // Hints from the previous peer wake and its drained timeout post
                // may remain. Start this forced callback interval with no tokens.
                var handle = sem_open(SharedWakeSignal.DarwinName(authority), 0);
                handle.Should().NotBe(new IntPtr(-1));
                try
                {
                    var drained = 0;
                    while (sem_trywait(handle) == 0) (++drained).Should().BeLessThan(16);
                    Marshal.GetLastWin32Error().Should().Be(35, "Darwin EAGAIN proves the hint queue is empty");
                }
                finally { sem_close(handle); }
            }
            peer.NativeWakeStage = stage =>
            {
                if (stage == "native-returned") returned.Set();
                if (stage != "timer-post") return;
                posting.Set();
                allowPost.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            // Windows exercises a real named-event wake well before its timeout.
            // Darwin queues the timeout post, then a peer wake returns sem_wait
            // while the timer still owns the native handle.
            var waiting = Task.Run(() => peer.Wait(darwin ? 1 : 30000));
            try
            {
                if (darwin) posting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                first.Wake();
                if (darwin)
                {
                    returned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    waiting.Wait(TimeSpan.FromMilliseconds(50)).Should().BeFalse(
                        "the queued timer must drain before its owner can close the semaphore");
                }
            }
            finally
            {
                allowPost.Set();
                first.Wake();
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                waiting.GetAwaiter().GetResult();
                peer.NativeWakeStage = null;
            }
        }
    }
}
#endif
