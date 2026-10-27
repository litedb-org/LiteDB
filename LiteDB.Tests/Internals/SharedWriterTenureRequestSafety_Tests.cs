#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Runtime.InteropServices;
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
            using var sampled = new ManualResetEventSlim();
            using var preempted = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var admitted = 0;
            Exception failure = null;
            var waiter = new Thread(() =>
            {
                var once = false;
                SharedWriterRequests.RequestStage = stage =>
                {
                    if (stage == "request-admitted") Interlocked.Increment(ref admitted);
                    if (stage != "request-sampled" || once) return;
                    once = true;
                    var now = Environment.TickCount64;
                    sampled.Set();
                    SpinWait.SpinUntil(() => authority.Map.Load(block + SharedMemoryLayout.RequestExpiry) >
                        now + SharedWriterRequests.ExpiryMilliseconds, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    preempted.Set();
                };
                try
                {
                    failure = Record.Exception(() =>
                    {
                        using var request = engine.MappedReaderTable.RequestWriter();
                        resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    });
                }
                finally { SharedWriterRequests.RequestStage = null; }
            }) { IsBackground = true };
            waiter.Start();
            try
            {
                sampled.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                preempted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Volatile.Read(ref admitted).Should().Be(0);
                older.Send("go"); older.Expect("waiting-main").GetAwaiter().GetResult();
                older.Expect("ready").GetAwaiter().GetResult();
                SpinWait.SpinUntil(() => Volatile.Read(ref admitted) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
                resume.Set();
                waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                older.Send("write 910"); older.Expect("wrote 910").GetAwaiter().GetResult();
                older.Send("close"); older.Expect("done").GetAwaiter().GetResult(); older.Finish().GetAwaiter().GetResult();
            }
            finally { resume.Set(); waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
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
                kill(stale.ProcessId, 19).Should().Be(0);
                try
                {
                    var expiry = authority.Map.Load(block + SharedMemoryLayout.RequestExpiry);
                    SpinWait.SpinUntil(() => Environment.TickCount64 > expiry + 10, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    using var successor = new MvccProcess("one-file-tenure-request", files.Filename, null);
                    successor.Expect("requested").GetAwaiter().GetResult();
                    var sequence = authority.Map.Load(SharedMemoryLayout.RequestSequence);
                    kill(stale.ProcessId, 18).Should().Be(0);
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
                finally { kill(stale.ProcessId, 18); }
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
                Exception failure = null;
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage => { if (stage == "request-published") published.Set(); };
                    try { failure = Record.Exception(() => target.BeginTrans()); }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                waiter.Start();
                try
                {
                    published.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    SpinWait.SpinUntil(() => table.Authority.Map.Load(block + SharedMemoryLayout.RequestExpiry) > expiry,
                        TimeSpan.FromSeconds(5)).Should().BeTrue();
                    target.Dispose();
                    waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    failure.Should().BeOfType<ObjectDisposedException>();
                    table.Closed.Should().BeTrue();
                }
                finally { target.Dispose(); waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
                older.Send("go"); older.Expect("waiting-main").GetAwaiter().GetResult(); older.Expect("ready").GetAwaiter().GetResult();
                older.Send("close"); older.Expect("done").GetAwaiter().GetResult(); older.Finish().GetAwaiter().GetResult();
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
        }

        private static long RemoteBlock(SharedMemoryAuthority authority) => Enumerable.Range(0, authority.Header.Processes)
            .Where(slot => slot != authority.Slot).Select(slot => SharedMemoryLayout.Blocks + (long)slot * authority.Header.Stride)
            .Single(block => authority.Map.Load(block) == 1 && authority.Map.Load(block + SharedMemoryLayout.TenureRequest) > 0);
        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
    }
}
#endif
