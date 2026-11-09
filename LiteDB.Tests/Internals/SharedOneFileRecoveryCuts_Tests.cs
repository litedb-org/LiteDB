#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileRecoveryCuts_Tests
    {
        public static IEnumerable<object[]> Cuts => new[]
        {
            "writer-epoch", "writer-owner", "recovery-owner", "fence-begin", "writer-begin", "open-begin",
            "open-structural-repaired", "recovery-cleared", "fence-end", "open-end", "write-released",
            "structural-begin", "structural-end", "startup-before-tail-trim", "startup-after-tail-trim"
        }.Select(stage => new object[] { stage });

        [OneFileTheory]
        [MemberData(nameof(Cuts))]
        public async Task Successors_dying_twice_inside_live_authority_repair_preserve_the_accepted_snapshot(string stage)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            using var old = engine.Query("docs", new Query());
            old.Read().Should().BeTrue();
            var table = engine.MappedReaderTable;
            var versions = table.LiveVersions();
            versions.Should().NotBeEmpty("an actual accepted snapshot must keep its mapped generation");
            var incarnation = table.Authority.Incarnation;
            var authority = table.Authority.Header.Authority;
            var tenure = table.Authority.Map.Load(SharedMemoryLayout.WriterTenureEpoch);
            var structural = table.Authority.Map.Load(SharedMemoryLayout.StructuralEpoch);
            var size = new FileInfo(files.Filename).Length;
            using (var tail = new FileStream(files.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                tail.SetLength(size + 123);
            for (var repair = 0; repair < 2; repair++)
            {
                // Reintroduce the same interrupted tail when a cut follows trimming.
                using (var tail = new FileStream(files.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    tail.SetLength(size + 123);
                using var successor = new MvccProcess("one-file-publication-hold", files.Filename, null, stage);
                await successor.Expect("ready");
                await successor.Kill();
                files.WriteIsFree();
                table.Authority.Header.Authority.Should().Be(authority, "DMS stayed alive; this is repair, not first-opener reset");
                table.Authority.Incarnation.Should().Be(incarnation);
            }
            db.Timeout.Should().BeGreaterThan(TimeSpan.Zero); // protected, synced repair
            SharedOneFilePublicationFixture.Quiet(table);
            table.LiveVersions().Should().BeEquivalentTo(versions);
            table.Authority.Map.Load(SharedMemoryLayout.WriterTenureEpoch).Should().BeGreaterThan(tenure);
            if (stage.StartsWith("structural-") || stage.StartsWith("startup-after"))
                table.Authority.Map.Load(SharedMemoryLayout.StructuralEpoch).Should().BeGreaterThan(structural);
            new FileInfo(files.Filename).Length.Should().Be(size);
            var rows = new List<BsonDocument>();
            do { rows.Add(old.Current.AsDocument); } while (old.Read());
            rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => SharedOneFilePublicationFixture.Row(id, 0)));
            for (var recovery = 0; recovery < 2; recovery++)
            {
                SharedOneFilePublicationFixture.Verify(db, 0);
                db.Checkpoint();
                SharedOneFilePublicationFixture.Quiet(table);
            }
        }

        [OneFileTheory]
        [InlineData("version-reset", 1)]
        [InlineData("slot-reused", 0)]
        public async Task Interrupted_reset_or_reuse_keeps_storage_and_repairs_the_same_authority(string stage, int expected)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var table = engine.MappedReaderTable;
            var identity = table.Authority.Header.Authority;
            var offset = stage == "version-reset" ? SharedMemoryLayout.ResetEpoch : SharedMemoryLayout.ReuseEpoch;
            var epoch = table.Authority.Map.Load(offset);
            using (var child = new MvccProcess("one-file-mutation-cut", files.Filename, null, stage))
            {
                await child.Expect("ready");
                await child.Kill();
            }
            table.Authority.Map.Load(offset).Should().BeGreaterThan(epoch);
            table.Authority.Header.Authority.Should().Be(identity);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                SharedOneFilePublicationFixture.Verify(db, expected);
                db.Checkpoint();
                SharedOneFilePublicationFixture.Quiet(table);
            }
        }

        [OneFileTheory]
        [InlineData("commit-version")]
        [InlineData("writer-end")]
        public async Task A_completed_durable_commit_survives_two_live_authority_recoveries(string stage)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using (var child = new MvccProcess("one-file-commit-cut", files.Filename, null, stage))
            {
                await child.Expect("ready");
                await child.Kill();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                SharedOneFilePublicationFixture.Verify(db, 1);
                db.Checkpoint();
                SharedOneFilePublicationFixture.Quiet(engine.MappedReaderTable);
            }
        }
    }
}
#endif
