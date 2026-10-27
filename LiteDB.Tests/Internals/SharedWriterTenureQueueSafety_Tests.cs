#if NET8_0_OR_GREATER
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureQueueSafety_Tests
    {
        [OneFileFact]
        public void Cancelling_a_queued_local_call_cannot_clear_the_published_head_or_strand_its_successor()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                var table = engine.MappedReaderTable;
                var authority = table.Authority;
                var head = table.RequestWriter();
                var ticket = authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest);
                var cancel = 0;
                Exception middleFailure = null, lastFailure = null;
                long lastTicket = 0;
                var middle = new Thread(() => middleFailure = Record.Exception(() =>
                {
                    using var request = table.RequestWriter(() => Volatile.Read(ref cancel) != 0);
                })) { IsBackground = true };
                var last = new Thread(() => lastFailure = Record.Exception(() =>
                {
                    using var request = table.RequestWriter();
                    lastTicket = authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest);
                })) { IsBackground = true };
                middle.Start();
                SpinWait.SpinUntil(() => authority.Requests.WaitingCount == 2, TimeSpan.FromSeconds(5)).Should().BeTrue();
                last.Start();
                try
                {
                    SpinWait.SpinUntil(() => authority.Requests.WaitingCount == 3, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    Volatile.Write(ref cancel, 1);
                    middle.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    middleFailure.Should().BeOfType<ObjectDisposedException>();
                    authority.Requests.WaitingCount.Should().Be(2);
                    authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest).Should().Be(ticket);
                }
                finally { Volatile.Write(ref cancel, 1); head.Dispose(); }
                last.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                lastFailure.Should().BeNull();
                lastTicket.Should().BeGreaterThan(ticket);
                authority.Requests.WaitingCount.Should().Be(0);
                authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest).Should().Be(0);
                engine.Update("docs", new[] { Row(1, 1) }).Should().Be(1);
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }

        [OneFileFact]
        public void Expired_ticket_exhaustion_on_the_renewal_timer_withdraws_priority_and_reports_to_the_waiter()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var published = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            {
                var table = engine.MappedReaderTable;
                var requests = table.Authority.Requests;
                var authority = table.Authority;
                var gate = Field<object>(requests, "_gate");
                Exception failure = null;
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-published") return;
                        published.Set(); resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    };
                    try { failure = Record.Exception(() => { using var request = table.RequestWriter(); }); }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                waiter.Start();
                var original = 0L;
                try
                {
                    published.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    lock (gate)
                    {
                        original = authority.Map.Load(SharedMemoryLayout.RequestSequence);
                        var expiry = authority.Map.Load(authority.Block + SharedMemoryLayout.RequestExpiry);
                        SpinWait.SpinUntil(() => Environment.TickCount64 > expiry, TimeSpan.FromSeconds(5)).Should().BeTrue();
                        authority.Map.Store(SharedMemoryLayout.RequestSequence, long.MaxValue);
                    }
                    SpinWait.SpinUntil(() => Field<Exception>(requests, "_failure") != null, TimeSpan.FromSeconds(5)).Should().BeTrue(
                        "the actual timer must catch the exhausted sequence rather than crash the process");
                    authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest).Should().Be(0);
                    authority.Map.Load(authority.Block + SharedMemoryLayout.RequestExpiry).Should().Be(0);
                }
                finally
                {
                    authority.Map.Store(SharedMemoryLayout.RequestSequence, original);
                    resume.Set(); waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                }
                failure.Should().BeOfType<InvalidOperationException>().Which.InnerException.Should().BeOfType<InvalidOperationException>();
                using (table.RequestWriter()) requests.Pending.Should().BeTrue();
                requests.Pending.Should().BeFalse();
                engine.Update("docs", new[] { Row(1, 1) }).Should().Be(1);
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }
    }
}
#endif
