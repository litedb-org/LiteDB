#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWalBatch_Tests
    {
        [OneFileFact]
        public void Every_safepoint_batch_publishes_one_reuse_before_its_first_physical_overwrite()
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.TransactionPageLimit = 1;
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans();
            var core = Core(engine);
            var state = State(core);
            var disk = Field<DiskService>(core, "_disk");
            var map = engine.MappedReaderTable.Authority.Map;
            var batchEpoch = map.Load(SharedMemoryLayout.ReuseEpoch);
            var batchLength = disk.GetFileLength(FileOrigin.Log);
            var overwrites = 0;
            var batches = 0;
            state.SimulateDiskWriteFail = page =>
            {
                if (page.Position >= batchLength) return;
                overwrites++;
                map.Load(SharedMemoryLayout.ReuseEpoch).Should().Be(batchEpoch + 1,
                    "mapped admission is excluded for the whole batch; invalidation must precede its first overwrite and coalesce within it");
            };
            state.AtCrashPoint = stage =>
            {
                if (stage != "wal-before-batch-flush") return;
                if (map.Load(SharedMemoryLayout.ReuseEpoch) > batchEpoch) batches++;
                batchEpoch = map.Load(SharedMemoryLayout.ReuseEpoch);
                batchLength = disk.GetFileLength(FileOrigin.Log);
            };
            try
            {
                for (var revision = 1; revision < 4; revision++)
                    db.GetCollection("docs").Update(Enumerable.Range(1, 160).Select(id => Row(id, revision)));
                db.Commit().Should().BeTrue();
                overwrites.Should().BeGreaterThan(0);
                batches.Should().BeGreaterThan(0);
            }
            finally { state.SimulateDiskWriteFail = null; state.AtCrashPoint = null; }
            for (var recovery = 0; recovery < 2; recovery++)
            {
                var expected = Enumerable.Range(1, 160).Select(id => Row(id, 3)).ToArray();
                db.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
                db.GetCollection("docs").Find(Query.EQ("value", 3)).Should().BeEquivalentTo(expected);
                db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
                db.Checkpoint();
                Quiet(engine.MappedReaderTable);
            }
        }

        [OneFileFact]
        public void Mapping_failure_with_an_accepted_peer_snapshot_preserves_both_files_and_the_snapshot()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var old = engine.Query("docs", new Query());
            old.Read().Should().BeTrue();
            var table = engine.MappedReaderTable;
            var versions = table.LiveVersions();
            versions.Should().NotBeEmpty();
            var data = File.ReadAllBytes(files.Filename);
            var wal = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
            var hits = 0;
            SharedMemoryAuthority.MapFile = (_, __) => { hits++; throw new UnauthorizedAccessException("mapped access denied"); };
            try
            {
                Action attach = () => SharedMemoryReaderTable.Open(files.Settings);
                attach.Should().Throw<UnauthorizedAccessException>();
                hits.Should().Be(1);
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(wal);
                table.LiveVersions().Should().BeEquivalentTo(versions);
            }
            finally { SharedMemoryAuthority.MapFile = null; }
            var rows = 0;
            do { old.Current["value"].AsInt32.Should().Be(0); old.Current["payload"].AsString.Should().Be(new string('x', 1024)); rows++; }
            while (old.Read());
            rows.Should().Be(160);
            Verify(db, 0);
        }
    }
}
#endif
