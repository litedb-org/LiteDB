#if NET8_0_OR_GREATER
using System;
using System.Linq;
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
    public class SharedWriterTenureRequestSafety_Tests
    {
        [OneFileFact]
        public void Renewal_after_a_preempted_clock_sample_preserves_end_to_end_remote_priority()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var authority = engine.MappedReaderTable.Authority;
            using var older = new MvccProcess("one-file-tenure-request", files.Filename, null);
            older.Expect("requested").GetAwaiter().GetResult();
            var block = RemoteBlock(authority);
            // Observe a real renewal after our clock sample, then freeze its
            // record before publishing the local ticket. A scheduler pause may
            // otherwise expire the peer and legitimately move it behind us.
            var sampledAt = Environment.TickCount64;
            SpinWait.SpinUntil(() => authority.Map.Load(block + SharedMemoryLayout.RequestExpiry) >
                sampledAt + SharedWriterRequests.ExpiryMilliseconds, TimeSpan.FromSeconds(5)).Should().BeTrue();
            SharedProcessSignals.Stop(older.ProcessId);
            var stopped = true;
            using var evaluated = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var admitted = 0;
            bool? staleSampleAdmitted = null;
            Exception failure = null;
            var waiter = new Thread(() =>
            {
                var once = false;
                SharedWriterRequests.RequestStage = stage =>
                {
                    if (stage == "request-admitted") Interlocked.Increment(ref admitted);
                    if (stage != "request-published" || once) return;
                    once = true;
                    authority.Map.Load(block + SharedMemoryLayout.TenureRequest).Should().BeGreaterThan(0)
                        .And.BeLessThan(authority.Map.Load(authority.Block + SharedMemoryLayout.TenureRequest));
                    authority.Map.Load(block + SharedMemoryLayout.RequestExpiry).Should()
                        .BeGreaterThan(sampledAt + SharedWriterRequests.ExpiryMilliseconds);
                    // Evaluate the actual admission predicate with the stale
                    // sample, without fabricating mapped bytes or clock values.
                    var predicate = typeof(SharedWriterRequests).GetMethod("IsTurn", BindingFlags.Instance | BindingFlags.NonPublic);
                    staleSampleAdmitted = (bool)predicate.Invoke(authority.Requests, new object[] { sampledAt });
                    evaluated.Set();
                    resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                };
                try
                {
                    failure = Record.Exception(() =>
                    {
                        using var request = engine.MappedReaderTable.RequestWriter();
                    });
                }
                finally { SharedWriterRequests.RequestStage = null; }
            }) { IsBackground = true };
            waiter.Start();
            try
            {
                evaluated.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                staleSampleAdmitted.Should().BeFalse("a real renewal after the clock sample cannot erase an older ticket");
                Volatile.Read(ref admitted).Should().Be(0);
                SharedProcessSignals.Resume(older.ProcessId);
                stopped = false;
                older.Send("go");
                // Both requests must make progress, including when the stopped
                // peer's wall-clock lease expires before it can resume.
                resume.Set();
                older.Expect("waiting-main").GetAwaiter().GetResult();
                older.Expect("ready").GetAwaiter().GetResult();
                waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Volatile.Read(ref admitted).Should().Be(1);
                older.Send("write 910"); older.Expect("wrote 910").GetAwaiter().GetResult();
                older.Send("close"); older.Expect("done").GetAwaiter().GetResult(); older.Finish().GetAwaiter().GetResult();
            }
            finally
            {
                if (stopped) SharedProcessSignals.Resume(older.ProcessId);
                resume.Set(); waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            failure.Should().BeNull();
            engine.Dispose();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            cold.GetCollection("tenure").Find(Query.EQ("value", 910)).Single()["payload"].AsString.Should().Be(new string('t', 256));
        }

        [OneFileFact]
        public void Expired_live_request_reenters_after_the_already_published_successor()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var stale = new MvccProcess("one-file-tenure-request", files.Filename, null))
            {
                stale.Expect("requested").GetAwaiter().GetResult();
                var authority = engine.MappedReaderTable.Authority;
                var block = RemoteBlock(authority);
                SharedProcessSignals.Stop(stale.ProcessId);
                var stopped = true;
                try
                {
                    var expiry = authority.Map.Load(block + SharedMemoryLayout.RequestExpiry);
                    SpinWait.SpinUntil(() => Environment.TickCount64 > expiry + 10, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    using var successor = new MvccProcess("one-file-tenure-request", files.Filename, null);
                    successor.Expect("requested").GetAwaiter().GetResult();
                    var sequence = authority.Map.Load(SharedMemoryLayout.RequestSequence);
                    SharedProcessSignals.Resume(stale.ProcessId);
                    stopped = false;
                    SpinWait.SpinUntil(() => authority.Map.Load(block + SharedMemoryLayout.TenureRequest) > sequence,
                        TimeSpan.FromSeconds(5)).Should().BeTrue("resuming renewal takes a new ticket instead of resurrecting expired priority");
                    stale.Send("go"); successor.Send("go");
                    successor.Expect("waiting-main").GetAwaiter().GetResult(); successor.Expect("ready").GetAwaiter().GetResult();
                    stale.Expect("waiting-main").GetAwaiter().GetResult(); stale.Expect("ready").GetAwaiter().GetResult();
                    foreach (var peer in new[] { successor, stale })
                    {
                        peer.Send("write " + (peer == successor ? 911 : 912));
                        peer.Expect("wrote " + (peer == successor ? 911 : 912)).GetAwaiter().GetResult();
                        peer.Send("close"); peer.Expect("done").GetAwaiter().GetResult(); peer.Finish().GetAwaiter().GetResult();
                    }
                }
                finally { if (stopped) SharedProcessSignals.Resume(stale.ProcessId); }
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            cold.GetCollection("tenure").FindAll().Select(d => d["_id"].AsInt32).OrderBy(id => id).Should().Equal(911, 912);
        }

        [OneFileFact]
        public void Final_disposal_cancels_a_request_behind_a_continuously_renewed_older_remote_ticket()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var target = new SharedEngine(files.Settings))
            using (var older = new MvccProcess("one-file-tenure-request", files.Filename, null))
            {
                older.Expect("requested").GetAwaiter().GetResult();
                var table = target.MappedReaderTable;
                var block = RemoteBlock(table.Authority);
                var expiry = table.Authority.Map.Load(block + SharedMemoryLayout.RequestExpiry);
                using var published = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                Exception failure = null;
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-published") return;
                        published.Set();
                        resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    };
                    try { failure = Record.Exception(() => target.BeginTrans()); }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                waiter.Start();
                try
                {
                    published.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    SpinWait.SpinUntil(() => table.Authority.Map.Load(block + SharedMemoryLayout.RequestExpiry) > expiry,
                        TimeSpan.FromSeconds(5)).Should().BeTrue();
                    var closing = System.Threading.Tasks.Task.Run(() => target.Dispose());
                    try
                    {
                        SpinWait.SpinUntil(() => Field<int>(target, "_disposed") != 0,
                            TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                    finally { resume.Set(); }
                    closing.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    failure.Should().BeOfType<ObjectDisposedException>();
                    table.Closed.Should().BeTrue();
                }
                finally { resume.Set(); target.Dispose(); waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
                older.Send("go"); older.Expect("waiting-main").GetAwaiter().GetResult(); older.Expect("ready").GetAwaiter().GetResult();
                older.Send("close"); older.Expect("done").GetAwaiter().GetResult(); older.Finish().GetAwaiter().GetResult();
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
        }

        private static long RemoteBlock(SharedMemoryAuthority authority) => Enumerable.Range(0, authority.Header.Processes)
            .Where(slot => slot != authority.Slot).Select(slot => SharedMemoryLayout.Blocks + (long)slot * authority.Header.Stride)
            .Single(block => authority.Map.Load(block) == 1 && authority.Map.Load(block + SharedMemoryLayout.TenureRequest) > 0);
    }
}
#endif
