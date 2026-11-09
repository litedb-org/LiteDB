#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
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
    public class SharedOneFileWarmRecoveryBreadth_Tests
    {
        public static IEnumerable<object[]> Cuts => SharedOneFileWarmRecovery_Tests.WriterCuts.Concat(new[] { new object[] { "writer-uncertain" } });

        [OneFileTheory]
        [MemberData(nameof(Cuts))]
        public async Task New_admission_at_each_death_cut_preserves_multiple_current_and_retired_warm_generations(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var peer = new SharedEngine(files.Settings);
            using var peerDb = new LiteDatabase(peer, disposeOnClose: false);
            peerDb.CheckpointSize = 0;
            using var child = new MvccProcess("one-file-warm-writer", files.Filename, null, cut);
            await child.Expect("ready");
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            using var oldest = engine.Query("docs", new Query());
            var table = engine.MappedReaderTable;
            var retired = table.ReadCache.Snapshot;
            await Task.Run(() => peerDb.GetCollection("docs").Update(Row(1, 1)));
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(1);
            var hits = engine.CoordinatedReadHits;
            using var current = engine.Query("docs", new Query());
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            var accepted = table.ReadCache.Snapshot;
            retired.Retired.Should().BeTrue();
            table.LiveVersions().Should().BeEquivalentTo(new[] { (int)retired.Status.Version, (int)accepted.Status.Version });
            child.Send("commit2");
            await child.Expect("cut");
            var before = engine.CoordinatedReadHits;
            var pending = Task.Run(() => db.GetCollection("docs").FindById(1));
            SpinWait.SpinUntil(() => Field<int>(engine, "_mutexWaiters") != 0, TimeSpan.FromSeconds(5)).Should().BeTrue(
                "a new admission at the cut must reject the old cache and enter protected recovery");
            engine.CoordinatedReadHits.Should().Be(before);
            await child.Kill();
            var expected = cut.StartsWith("wal-") || cut == "commit-version" || cut == "writer-end" || cut == "writer-uncertain" ? 2 : 1;
            (await pending)["value"].AsInt32.Should().Be(expected);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Verify(db, expected);
                await Task.Run(peerDb.Checkpoint);
                peer.MutexOwner.WaitForRelease();
                Quiet(table);
                table.LiveVersions().Should().Contain(new[] { (int)retired.Status.Version, (int)accepted.Status.Version });
            }
            Rows(oldest).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            Rows(current).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? 1 : 0)));
        }

        [OneFileFact]
        public async Task Dead_multigeneration_reader_slot_is_reused_while_an_independent_scanner_remains_active()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            using var local = engine.Query("docs", new Query());
            var table = engine.MappedReaderTable;
            var localVersion = (int)table.ReadCache.Snapshot.Status.Version;
            using var scannerStop = new CancellationTokenSource();
            var scans = 0;
            var scanner = Task.Run(() =>
            {
                while (!scannerStop.IsCancellationRequested)
                {
                    var live = table.LiveVersions();
                    if (live != null) live.Should().Contain(localVersion);
                    Interlocked.Increment(ref scans);
                    Thread.Yield();
                }
            });
            try
            {
                using var first = new MvccProcess("one-file-warm-generations", files.Filename, null, "1");
                await first.Expect("ready");
                var identity = await first.ReadLine(TimeSpan.FromSeconds(5));
                var firstVersions = (await first.ReadLine(TimeSpan.FromSeconds(5))).Split(',').Select(int.Parse).ToArray();
                firstVersions.Should().HaveCount(2).And.OnlyHaveUniqueItems();
                table.LiveVersions().Should().BeEquivalentTo(firstVersions.Append(localVersion));
                var reached = Volatile.Read(ref scans);
                await first.Kill();
                table.LiveVersions().Should().Equal(localVersion);
                using var second = new MvccProcess("one-file-warm-generations", files.Filename, null, "2");
                await second.Expect("ready");
                var replacement = await second.ReadLine(TimeSpan.FromSeconds(5));
                replacement.Split(':')[0].Should().Be(identity.Split(':')[0]);
                replacement.Should().NotBe(identity);
                var secondVersions = (await second.ReadLine(TimeSpan.FromSeconds(5))).Split(',').Select(int.Parse).ToArray();
                secondVersions.Should().HaveCount(2).And.OnlyHaveUniqueItems();
                table.LiveVersions().Should().BeEquivalentTo(secondVersions.Append(localVersion));
                SpinWait.SpinUntil(() => Volatile.Read(ref scans) > reached + 10, TimeSpan.FromSeconds(5)).Should().BeTrue();
                await second.Kill();
                table.LiveVersions().Should().Equal(localVersion);
                Rows(local).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                scannerStop.Cancel();
                await scanner;
                local.Dispose();
                for (var repeat = 0; repeat < 2; repeat++) { Verify(db, 2); db.Checkpoint(); }
            }
            finally { scannerStop.Cancel(); await scanner; }
        }

        private static List<BsonDocument> Rows(IBsonDataReader reader)
        {
            var rows = new List<BsonDocument>();
            while (reader.Read()) rows.Add(reader.Current.AsDocument);
            return rows;
        }
    }
}
#endif
