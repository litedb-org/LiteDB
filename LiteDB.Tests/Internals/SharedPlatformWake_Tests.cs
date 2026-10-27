#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
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
                    VerifyWake(first, peer);
                }
                finally { SharedWakeSignal.Retire(authority); }
            }
        }

        private static void VerifyWake(SharedWakeSignal first, SharedWakeSignal peer)
        {
            using var posting = new ManualResetEventSlim();
            using var allowPost = new ManualResetEventSlim();
            using var returned = new ManualResetEventSlim();
            var darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
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
