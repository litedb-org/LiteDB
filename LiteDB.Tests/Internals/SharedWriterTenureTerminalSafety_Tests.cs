#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureTerminalSafety_Tests
    {
        [OneFileTheory]
        [InlineData("tenure-handoff")] [InlineData("core-closed")]
        [InlineData("writer-end")] [InlineData("write-released")] [InlineData("main-release")]
        public void Repeated_close_death_with_old_snapshots_and_pending_local_reservations_preserves_exact_commits(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var reader = new SharedEngine(files.Settings))
            using (var readerDb = new LiteDatabase(reader, disposeOnClose: false))
            {
                SharedOneFileWarmReads_Tests.Warm(reader, readerDb);
                using var old = reader.Query("docs", new Query());
                old.Read().Should().BeTrue();
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var child = new MvccProcess("one-file-tenure-close-cut", files.Filename, null, cut);
                    child.Expect("ready").GetAwaiter().GetResult();
                    using var first = new SharedEngine(files.Settings);
                    using var second = new SharedEngine(files.Settings);
                    using var published = new ManualResetEventSlim();
                    using var secondEntered = new ManualResetEventSlim();
                    var failures = new Exception[2];
                    Thread Worker(SharedEngine target, int slot) => new Thread(() =>
                    {
                        SharedWriterRequests.RequestStage = stage => { if (slot == 0 && stage == "request-published") published.Set(); };
                        try
                        {
                            failures[slot] = Record.Exception(() =>
                            {
                                if (slot == 1) secondEntered.Set();
                                target.BeginTrans().Should().BeTrue();
                                target.Update("docs", new[] { Row(2 + slot, 2 + slot) }).Should().Be(1);
                                target.Commit().Should().BeTrue();
                            });
                        }
                        finally { SharedWriterRequests.RequestStage = null; }
                    }) { IsBackground = true };
                    var pending = Worker(first, 0);
                    var queued = Worker(second, 1);
                    pending.Start();
                    try
                    {
                        published.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        queued.Start();
                        secondEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        Field<SharedLocalTransactionOwner.Lease>(first.MappedReaderTable.LocalTransactions, "_current").Thread.Should().BeSameAs(pending);
                        child.Send("close"); child.Expect("cut").GetAwaiter().GetResult();
                        child.Kill(sharedFilesRemainOpen: true).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        if (!pending.Join(TimeSpan.FromSeconds(10))) child.Kill(sharedFilesRemainOpen: true).GetAwaiter().GetResult();
                        pending.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        if ((queued.ThreadState & ThreadState.Unstarted) == 0) queued.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                    failures.Should().OnlyContain(error => error == null);
                    first.Dispose(); second.Dispose();
                    files.WriteIsFree();
                }
                new[] { old.Current.AsDocument }.Concat(Rows(old)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(files.Filename);
                var expected = Enumerable.Range(1, 160).Select(id => Row(id, id <= 3 ? id : 0)).ToArray();
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
                foreach (var value in new[] { 1, 2, 3 })
                    cold.GetCollection("docs").Find(Query.EQ("value", value)).Should().BeEquivalentTo(new[] { Row(value, value) });
                cold.GetCollection("docs").Find(Query.EQ("value", 99)).Should().BeEmpty();
                cold.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
            }
        }

        private static IEnumerable<BsonDocument> Rows(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
    }
}
#endif
