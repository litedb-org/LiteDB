#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureReview_Tests
    {
        [OneFileFact]
        public void Reacquiring_holder_takes_a_fresh_ticket_even_with_an_outstanding_local_waiter()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var holder = new SharedEngine(files.Settings))
            using (var local = new SharedEngine(files.Settings))
            using (var requested = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            {
                holder.Update("docs", new[] { Row(1, 1) });
                var authority = holder.MappedReaderTable.Authority;
                Exception localFailure = null;
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-published") return;
                        requested.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("local request barrier");
                    };
                    try
                    {
                        localFailure = Record.Exception(() =>
                        {
                            local.BeginTrans().Should().BeTrue();
                            local.Rollback().Should().BeTrue();
                        });
                    }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                waiter.Start();
                long first = 0, reacquired = 0;
                var admissionHits = 0;
                try
                {
                    requested.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    first = authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest);
                    using var remote = new MvccProcess("one-file-tenure-request", files.Filename, null);
                    remote.Expect("requested").GetAwaiter().GetResult();
                    var remoteBlock = FindRemoteBlock(authority);
                    authority.Map.Load(remoteBlock + SharedMemoryLayout.TenureRequest).Should().BeGreaterThan(first);
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-admitted") return;
                        admissionHits++;
                        reacquired = authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest);
                    };
                    // Each forced handoff really closes the core and releases WRITE/main.
                    // A continuously outstanding local request must not preserve the
                    // releasing holder's priority over the remote process forever.
                    for (var n = 0; n < 12; n++)
                    {
                        var pin = holder.Pin;
                        if (pin != null) { pin.RequestRelease(false); pin.WaitReleased(); }
                        holder.Update("docs", new[] { Row(1, n + 2) }).Should().Be(1);
                    }
                    remote.Send("go");
                    // Unblock the older local call before joining the remote's admission.
                    resume.Set();
                    waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    remote.Expect("waiting-main").GetAwaiter().GetResult();
                    remote.Expect("ready").GetAwaiter().GetResult();
                    remote.Send("close"); remote.Expect("done").GetAwaiter().GetResult();
                    remote.Finish().GetAwaiter().GetResult();
                }
                finally
                {
                    SharedWriterRequests.RequestStage = null;
                    resume.Set();
                    waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                }
                localFailure.Should().BeNull();
                admissionHits.Should().Be(12);
                reacquired.Should().BeGreaterThan(first,
                    "a releasing holder must take a fresh ticket instead of borrowing an indefinitely renewed local waiter's priority");
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 13);
        }

        [OneFileFact]
        public void Renewing_an_older_request_after_a_clock_sample_does_not_erase_its_priority()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var authority = engine.MappedReaderTable.Authority;
            using var remote = new MvccProcess("one-file-tenure-request", files.Filename, null);
            remote.Expect("requested").GetAwaiter().GetResult();
            var block = FindRemoteBlock(authority);
            bool? admitted = null;
            SharedWriterRequests.RequestStage = stage =>
            {
                if (stage != "request-published") return;
                var sampled = Environment.TickCount64;
                // Model preemption after IsTurn's caller samples now, using real
                // peer renewals rather than injected/fabricated expiry records.
                SpinWait.SpinUntil(() => authority.Map.Load(block + SharedMemoryLayout.RequestExpiry) >
                    sampled + SharedWriterRequests.ExpiryMilliseconds, TimeSpan.FromSeconds(5)).Should().BeTrue();
                var predicate = typeof(SharedWriterRequests).GetMethod("IsTurn", BindingFlags.Instance | BindingFlags.NonPublic);
                admitted = (bool)predicate.Invoke(authority.Requests, new object[] { sampled });
                throw new IOException("release review request");
            };
            try
            {
                ((Action)(() => { using var request = engine.MappedReaderTable.RequestWriter(); }))
                    .Should().Throw<IOException>().WithMessage("release review request");
            }
            finally { SharedWriterRequests.RequestStage = null; }
            remote.Send("go"); remote.Expect("waiting-main").GetAwaiter().GetResult();
            remote.Expect("ready").GetAwaiter().GetResult();
            remote.Send("close"); remote.Expect("done").GetAwaiter().GetResult();
            remote.Finish().GetAwaiter().GetResult();
            admitted.Should().BeFalse("a fresh renewal cannot invalidate a live older ticket when a requester was descheduled after sampling the clock");
        }

        private static long FindRemoteBlock(SharedMemoryAuthority authority)
        {
            for (var slot = 0; slot < authority.Header.Processes; slot++)
            {
                if (slot == authority.Slot) continue;
                var block = SharedMemoryLayout.Blocks + (long)slot * authority.Header.Stride;
                if (authority.Map.Load(block) == 1 && authority.Map.Load(block + SharedMemoryLayout.TenureRequest) > 0)
                    return block;
            }
            throw new InvalidOperationException("The child did not publish a request.");
        }
    }
}
#endif
