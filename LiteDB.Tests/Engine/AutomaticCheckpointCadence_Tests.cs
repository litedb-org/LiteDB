using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class AutomaticCheckpointCadence_Tests
    {
        [Theory]
        [InlineData(null, 1)]
        [InlineData(null, 127)]
        [InlineData(null, 128)]
        [InlineData(null, 129)]
        [InlineData(null, 1000)]
        [InlineData("secret", 127)]
        [InlineData("secret", 128)]
        [InlineData("secret", 129)]
        [InlineData("secret", 1000)]
        public void Automatic_checkpoint_waits_until_logical_pages_exceed_the_pragma(string password, int threshold)
        {
            using var file = new TempFile();
            using (var engine = Open(file, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db, threshold);
                var disk = Disk(engine);
                for (var value = 1; value <= threshold; value++)
                {
                    // Each header-only transaction appends exactly one logical page.
                    db.UserVersion = value;
                    disk.GetFileLength(FileOrigin.Log).Should().Be((long)value * PAGE_SIZE);
                }
                var raw = ((ChecksummedWalStream)disk.WalWriterLock).RawStream;
                raw.Length.Should().Be(WalPadding.AlignedLength((long)threshold * WalChecksum.FrameSize));
                raw.Length.Should().BeGreaterThan((long)threshold * PAGE_SIZE,
                    "checksum trailers and aligned EOF must not trigger maintenance");
                db.UserVersion = threshold + 1;
                disk.GetFileLength(FileOrigin.Log).Should().Be(0, "the first excess page triggers reclamation");
                Verify(db, threshold + 1);
            }
            Reopen(file, password, threshold + 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Large_checkpoint_pragma_does_not_overflow_into_checkpointing_every_commit(string password)
        {
            using var file = new TempFile();
            using (var engine = Open(file, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db, int.MaxValue);
                for (var value = 1; value <= 3; value++) db.UserVersion = value;
                Disk(engine).GetFileLength(FileOrigin.Log).Should().Be(3L * PAGE_SIZE);
                db.Checkpoint();
                Disk(engine).GetFileLength(FileOrigin.Log).Should().Be(0);
            }
            Reopen(file, password, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Volatile_WAL_override_still_checkpoints_a_single_header_page(string password)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, DurableCommits = false, CheckpointEachCommit = true
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db, 1000);
                db.UserVersion = 1;
                Disk(engine).GetFileLength(FileOrigin.Log).Should().Be(0,
                    "the volatile-WAL override is independent of the persisted page threshold");
                db.CheckpointSize.Should().Be(1000);
            }
            Reopen(file, password, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Failed_automatic_checkpoint_at_first_excess_page_stops_writes_and_recovers(string password)
        {
            using var file = new TempFile();
            using (var engine = Open(file, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db, 2);
                var writes = 0;
                engine.SimulateDataWriteFail = page =>
                {
                    writes++;
                    throw new IOException("automatic checkpoint write failed");
                };
                db.UserVersion = 1;
                db.UserVersion = 2;
                writes.Should().Be(0);
                Action commit = () => db.UserVersion = 3;
                commit.Should().Throw<IOException>().WithMessage("automatic checkpoint write failed");
                writes.Should().Be(1);
                Action later = () => db.UserVersion = 4;
                later.Should().Throw<Exception>();
                writes.Should().Be(1);
            }
            // The triggering transaction reached the OS cache before maintenance failed.
            // Interrupt two subsequent recovery checkpoints, retaining the complete
            // committed state and WAL until a later attempt can finish.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var engine = Open(file, password);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 3);
                var writes = 0;
                engine.SimulateDataWriteFail = page =>
                {
                    writes++;
                    throw new IOException("recovery checkpoint interrupted");
                };
                Action retry = () => db.Checkpoint();
                retry.Should().Throw<IOException>().WithMessage("recovery checkpoint interrupted");
                writes.Should().Be(1);
            }
            Reopen(file, password, 3);
        }

        private static LiteEngine Open(TempFile file, string password, bool readOnly = false) => new LiteEngine(new EngineSettings
        {
            Filename = file.Filename, Password = password, DurableCommits = false, ReadOnly = readOnly
        });

        private static void Seed(LiteDatabase db, int threshold)
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Documents());
            rows.EnsureIndex("value");
            db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "unchanged" });
            db.CheckpointSize = threshold;
            db.Checkpoint();
        }

        private static void Verify(LiteDatabase db, int version)
        {
            db.UserVersion.Should().Be(version);
            db.GetCollection("rows").FindAll().Should().BeEquivalentTo(Documents());
            db.GetCollection("rows").Find(Query.EQ("value", 1)).Select(row => row["_id"].AsInt32)
                .Should().BeEquivalentTo(new[] { 1, 3, 5, 7 });
            db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("unchanged");
        }

        private static void Reopen(TempFile file, string password, int version)
        {
            for (var open = 0; open < 3; open++)
            {
                var before = TempFile.ReadAllBytesShared(file.Filename);
                var readOnly = open == 1;
                using var engine = Open(file, password, readOnly);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, version);
                if (readOnly) TempFile.ReadAllBytesShared(file.Filename).Should().Equal(before);
                else db.Checkpoint();
            }
        }

        private static BsonDocument[] Documents() => Enumerable.Range(0, 8).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 2, ["payload"] = new string('x', 1024)
        }).ToArray();

        private static DiskService Disk(LiteEngine engine) => (DiskService)typeof(LiteEngine)
            .GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
    }
}
