#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureProcess_Tests
    {
        [OneFileFact]
        public async Task Disposing_the_last_facade_while_a_request_waits_keeps_its_mapping_until_unwind()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var holder = new MvccProcess("one-file-tenure", files.Filename, null))
            using (var target = new SharedEngine(files.Settings))
            {
                await holder.Expect("ready");
                holder.Send("begin 999"); await holder.Expect("begun");
                var table = target.MappedReaderTable;
                using var requested = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                Exception failure = null;
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-published") return;
                        requested.Set(); resume.Wait();
                    };
                    try { failure = Record.Exception(() => target.BeginTrans()); }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                waiter.Start();
                try
                {
                    requested.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    target.Dispose();
                    table.Closed.Should().BeFalse("the pending request owns a participant reference");
                    table.Authority.Map.Load(table.Authority.Block + SharedMemoryLayout.TenureRequest).Should().BeGreaterThan(0);
                }
                finally { resume.Set(); }
                waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                failure.Should().BeOfType<ObjectDisposedException>();
                table.Closed.Should().BeTrue();
                holder.Send("rollback"); await holder.Expect("rolled-back");
                holder.Send("close"); await holder.Expect("done"); await holder.Finish();
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            cold.GetCollection("tenure").FindAll().Should().BeEmpty();
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Dead_or_expired_requester_cannot_reserve_a_turn(bool expireAlive)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var holder = new SharedEngine(files.Settings))
            {
                holder.BeginTrans(); holder.Update("docs", new[] { Row(1, 1) });
                using var stale = new MvccProcess("one-file-tenure-request", files.Filename, null);
                stale.Expect("requested").GetAwaiter().GetResult();
                if (expireAlive)
                {
                    kill(stale.ProcessId, 19).Should().Be(0, "SIGSTOP freezes a live requester's renewal timer");
                    Task.Delay(SharedWriterRequests.ExpiryMilliseconds * 3).GetAwaiter().GetResult();
                }
                else stale.Kill(sharedFilesRemainOpen: true).GetAwaiter().GetResult();
                using var successor = new MvccProcess("one-file-tenure-request", files.Filename, null);
                successor.Expect("requested").GetAwaiter().GetResult();
                successor.Send("go"); successor.Expect("waiting-main").GetAwaiter().GetResult();
                holder.Commit();
                successor.Expect("ready").GetAwaiter().GetResult();
                successor.Send("write 902"); successor.Expect("wrote 902").GetAwaiter().GetResult();
                successor.Send("close"); successor.Expect("done").GetAwaiter().GetResult(); successor.Finish().GetAwaiter().GetResult();
                if (expireAlive) stale.Kill(sharedFilesRemainOpen: true).GetAwaiter().GetResult();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
                cold.GetCollection("tenure").FindAll().Select(d => d["_id"].AsInt32).Should().Equal(902);
            }
        }

        [OneFileFact]
        public async Task Dead_holder_with_a_waiting_requester_recovers_commits_and_discards_aborted_rows()
        {
            using var files = new SharedOneFilePublicationFixture();
            // Keep a real participant and accepted generation alive across death.
            using (var reader = new SharedEngine(files.Settings))
            using (var snapshot = reader.Query("docs", new Query()))
            {
                snapshot.Read().Should().BeTrue();
                using var holder = new MvccProcess("one-file-tenure", files.Filename, null);
                await holder.Expect("ready");
                holder.Send("write 903"); await holder.Expect("wrote 903");
                holder.Send("begin 999"); await holder.Expect("begun");
                using var successor = new MvccProcess("one-file-tenure-request", files.Filename, null);
                await successor.Expect("requested");
                successor.Send("go"); await successor.Expect("waiting-main");
                await holder.Kill(sharedFilesRemainOpen: true);
                await successor.Expect("ready");
                successor.Send("write 904"); await successor.Expect("wrote 904");
                successor.Send("close"); await successor.Expect("done"); await successor.Finish();
                var rows = new[] { snapshot.Current }.Concat(ReadRest(snapshot)).ToArray();
                rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                using var warm = reader.Query("docs", new Query());
                ReadRest(warm).Should().HaveCount(160);
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
                cold.GetCollection("tenure").FindAll().Select(d => d["_id"].AsInt32).OrderBy(id => id).Should().Equal(903, 904);
                cold.GetCollection("tenure").Find(Query.EQ("value", 999)).Should().BeEmpty();
            }
        }

        [OneFileFact]
        public async Task Three_contending_processes_each_make_progress_and_preserve_every_commit()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var first = new MvccProcess("one-file-tenure", files.Filename, null))
            using (var second = new MvccProcess("one-file-tenure", files.Filename, null))
            using (var third = new MvccProcess("one-file-tenure", files.Filename, null))
            {
                await Task.WhenAll(first.Expect("ready"), second.Expect("ready"), third.Expect("ready"));
                first.Send("loop 1"); second.Send("loop 2"); third.Send("loop 3");
                async Task Progress(MvccProcess process, int worker)
                {
                    // Per-worker deadlines advance only after this worker commits.
                    for (var n = 0; n < 24; n++) await process.Expect("wrote " + (worker * 100 + n));
                    await process.Expect("loop-done");
                }
                await Task.WhenAll(Progress(first, 1), Progress(second, 2), Progress(third, 3));
                foreach (var process in new[] { first, second, third }) process.Send("close");
                await Task.WhenAll(first.Expect("done"), second.Expect("done"), third.Expect("done"));
                await Task.WhenAll(first.Finish(), second.Finish(), third.Finish());
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            var expected = Enumerable.Range(1, 3).SelectMany(w => Enumerable.Range(0, 24).Select(n => w * 100 + n)).ToArray();
            var actual = cold.GetCollection("tenure").FindAll().OrderBy(d => d["_id"].AsInt32).ToArray();
            actual.Select(d => d["_id"].AsInt32).Should().Equal(expected);
            actual.Should().OnlyContain(d => d["value"] == d["_id"] && d["payload"].AsString == new string('t', 256));
        }

        private static System.Collections.Generic.IEnumerable<BsonDocument> ReadRest(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
    }
}
#endif
