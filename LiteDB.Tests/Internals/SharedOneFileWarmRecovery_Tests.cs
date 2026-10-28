#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmRecovery_Tests
    {
        public static IEnumerable<object[]> WriterCuts => new[]
        {
            "writer-epoch", "writer-owner", "recovery-owner", "fence-begin", "writer-begin", "open-begin",
            "open-structural-repaired", "recovery-cleared", "fence-end", "open-end",
            "wal-before-durable-flush", "wal-after-durable-flush", "commit-version", "writer-end"
        }.Select(stage => new object[] { stage });

        [OneFileTheory]
        [MemberData(nameof(WriterCuts))]
        public async Task Death_at_every_writer_fence_keeps_warm_generations_during_repeated_protected_recovery(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            using var writer = new MvccProcess("one-file-warm-writer", files.Filename, null, cut);
            await writer.Expect("ready");
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var cache = engine.MappedReaderTable.ReadCache.Snapshot;
            var hits = engine.CoordinatedReadHits;
            using var old = engine.Query("docs", new Query());
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            old.Read().Should().BeTrue();
            var table = engine.MappedReaderTable;
            var oldVersion = cache.Status.Version;
            table.LiveVersions().Should().Contain((int)oldVersion);
            writer.Send("commit");
            await writer.Expect("cut");
            var valid = table.Reads.TryRead(out var status);
            // At quiet opening boundaries only the independently synced old prefix
            // is available. At commit publication, the fence remains odd until done.
            if (valid)
            {
                status.Identity.Should().NotBe(cache.Status.Identity, "pre-mutation tenure invalidates idle cache authority");
                if (cut == "writer-end") status.Version.Should().BeGreaterThan(oldVersion);
                else status.Version.Should().Be(oldVersion);
            }
            if (cut == "commit-version") valid.Should().BeFalse();
            await writer.Kill();
            files.WriteIsFree();
            for (var recovery = 0; recovery < 2; recovery++)
            {
                db.Timeout.Should().BeGreaterThan(TimeSpan.Zero); // synced writable recovery, never cached metadata
                Quiet(table);
                table.LiveVersions().Should().Contain((int)oldVersion);
                var committed = cut.StartsWith("wal-") || cut == "commit-version" || cut == "writer-end" ? 1 : 0;
                Verify(db, committed);
                db.Checkpoint();
            }
            var rows = new List<BsonDocument>();
            do { rows.Add(old.Current.AsDocument); } while (old.Read());
            rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            old.Dispose();
            table.HasReaders.Should().BeFalse();
        }

        [OneFileFact]
        public async Task Killed_warm_reader_releases_kernel_liveness_and_its_generation_can_be_reclaimed()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var child = new MvccProcess("one-file-warm-reader", files.Filename, null);
            await child.Expect("ready");
            engine.MappedReaderTable.LiveVersions().Should().NotBeEmpty();
            db.GetCollection("docs").Update(Row(1, 1)).Should().BeTrue();
            db.Checkpoint();
            await child.Kill();
            engine.MappedReaderTable.LiveVersions().Should().BeEmpty("dead mapped records are ignored only after kernel proof");
            for (var recovery = 0; recovery < 2; recovery++) { db.Checkpoint(); Verify(db, 1); }
        }
    }
}
#endif
