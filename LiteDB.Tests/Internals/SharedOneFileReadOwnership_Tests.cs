#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileReadOwnership_Tests
    {
        [OneFileTheory]
        [InlineData("buffered")] [InlineData("streamed")]
        [InlineData("transform")] [InlineData("pinned")]
        public void Same_thread_foreign_transaction_refuses_every_cold_acquisition(string path)
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            if (path == "transform") settings.ReadTransform = (_, value) => value;
            using var owner = new SharedEngine(settings);
            using var peer = new SharedEngine(settings);
            owner.PinIdleLimit = owner.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = path == "pinned" ? owner.Query("docs", new Query()) : null;
            if (anchor != null) { anchor.Read().Should().BeTrue(); owner.Update("docs", new[] { Row(1, 0) }); }
            owner.BeginTrans().Should().BeTrue();
            owner.Update("docs", new[] { Row(1, 1) });
            using var done = new ManualResetEventSlim();
            // A watchdog disposes only on a broken implementation, so the assertion
            // fails without leaving the test host deadlocked on its own transaction.
            var watchdog = new Thread(() => { if (!done.Wait(TimeSpan.FromSeconds(3))) owner.Dispose(); })
                { IsBackground = true };
            watchdog.Start();
            Exception error;
            try
            {
                error = Record.Exception(() =>
                {
                    using var result = peer.Query("docs", new Query { Limit = path == "buffered" ? 1 : int.MaxValue });
                    while (result.Read()) { }
                });
            }
            finally { done.Set(); watchdog.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
            error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("connection");
            owner.Rollback().Should().BeTrue();
            owner.Dispose(); peer.Dispose(); anchor?.Dispose();
            using var cold = new LiteDatabase(files.Filename);
            Verify(cold, 0);
        }

        [OneFileFact]
        public void An_independent_warm_committed_snapshot_remains_readable_during_a_same_thread_transaction()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var owner = new SharedEngine(files.Settings);
            using var reader = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(reader, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(reader, db);
            using var accepted = reader.Query("docs", new Query());
            accepted.Read().Should().BeTrue();
            var hits = reader.CoordinatedReadHits;
            using (var independent = reader.Query("docs", new Query()))
            {
                owner.BeginTrans().Should().BeTrue();
                owner.Update("docs", new[] { Row(1, 1) });
                var count = 0;
                while (independent.Read())
                {
                    independent.Current.Should().BeEquivalentTo(Row(++count, 0));
                }
                count.Should().Be(160);
            }
            reader.CoordinatedReadHits.Should().BeGreaterThan(hits);
            var remaining = 1;
            while (accepted.Read()) { accepted.Current.Should().BeEquivalentTo(Row(++remaining, 0)); }
            remaining.Should().Be(160);
            owner.Rollback().Should().BeTrue();
            Verify(db, 0);
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Other_thread_cold_reader_waits_and_resumes_after_commit_or_rollback(bool pinned, bool commit)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var owner = new SharedEngine(files.Settings);
            using var peer = new SharedEngine(files.Settings);
            owner.PinIdleLimit = owner.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = pinned ? owner.Query("docs", new Query()) : null;
            if (anchor != null) { anchor.Read().Should().BeTrue(); owner.Update("docs", new[] { Row(1, 0) }); }
            owner.BeginTrans().Should().BeTrue();
            owner.Update("docs", new[] { Row(1, 1) });
            Exception failure = null;
            var thread = new Thread(() => failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(peer, disposeOnClose: false);
                Verify(db, commit ? 1 : 0);
            })) { IsBackground = true };
            thread.Start();
            try
            {
                SpinWait.SpinUntil(() => Field<int>(peer, "_mutexWaiters") > 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                thread.IsAlive.Should().BeTrue();
            }
            finally
            {
                if (commit) owner.Commit().Should().BeTrue(); else owner.Rollback().Should().BeTrue();
                if (pinned) { owner.Pin?.RequestRelease(force: false); anchor.Dispose(); }
                thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            failure.Should().BeNull();
        }
    }
}
#endif
