using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A format promotion (here v11 to v12 by the first compact write) journals the data header
    /// into the WAL, then overwrites it. When that header write failed part-way with an exception
    /// the engine does not treat as fatal (any non-I/O exception, e.g. UnauthorizedAccessException),
    /// the engine kept running with the journal outstanding: the rollback's next WAL append was
    /// refused, and its cleanup truncated the WAL to its logical length, dropping the journal, the
    /// torn header's only recovery copy. The database could not be opened again (checksum mismatch
    /// in the data header). Such a failure now stops the engine before the WAL writer is released,
    /// and a failed append never truncates an outstanding journal. The failure is recorded
    /// (decision 6 of docs/decisions/durability-policy.md): the engine's next call reopens it
    /// read-only, so it reads the rows and refuses writes without touching the journal.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class FailedPromotionJournal_Tests
    {
        [Fact]
        public void Torn_promotion_header_stays_recoverable_from_its_journal()
        {
            using var data = new TornHeaderData();
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy })))
            {
                db.GetCollection("rows").Insert(Enumerable.Range(1, 5).Select(id => new BsonDocument { ["_id"] = id }));
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);

            var settings = new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Auto };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                data.Armed = true;
                Action compact = () => db.GetCollection("compact").Insert(Compact());
                var thrown = compact.Should().Throw<IOException>("the promotion stops the engine with the header journal outstanding")
                    .WithMessage("File format promotion failed*").Which;
                thrown.InnerException.Should().BeOfType<UnauthorizedAccessException>();
                data.Torn.Should().BeTrue();

                // The engine continues read-only (decision 6): it reads the rows through the torn header's
                // journal, reports the failure, and refuses a write with it, and neither changes a byte.
                var files = (Data: data.ToArray(), Log: log.ToArray());
                db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3, 4, 5);
                db.GetCollectionNames().Should().Equal("rows");
                var info = db.GetCollection("$database").FindAll().Single();
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["writeFailure"]["operation"].AsString.Should().Be("A file format promotion");
                info["writeFailure"]["error"].AsString.Should().Be("File format promotion failed.");
                info["writeFailure"]["walKept"].AsBoolean.Should().BeTrue("the log file holds the WAL and the header journal");
                info["readOnlyReason"].AsString.Should().StartWith("A file format promotion failed at ").And.EndWith(
                    ": File format promotion failed. The log file was kept.");
                Action later = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 6 });
                var refused = later.Should().Throw<IOException>().Which;
                refused.Message.Should().Be(LiteEngine.WriteFailedPrefix + info["readOnlyReason"].AsString);
                refused.InnerException.Should().BeSameAs(thrown, "the refusal carries the recorded failure");
                db.GetCollection("rows").Count().Should().Be(5);
                data.ToArray().Should().Equal(files.Data, "the read-only engine writes nothing");
                log.ToArray().Should().Equal(files.Log, "the journal stays");
            }

            // A killed process leaves every byte the streams hold.
            using (var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(data.ToArray()), LogStream = new MemoryStream(log.ToArray())
            })))
            {
                recovered.GetCollection("rows").Count().Should().Be(5, "the header is restored from its journal");
            }

            using var reopened = new LiteDatabase(new LiteEngine(settings));
            reopened.GetCollection("rows").Count().Should().Be(5);
            reopened.GetCollection("compact").Insert(Compact());
            reopened.GetCollection("compact").Count().Should().Be(10);
        }

        /// <summary>
        /// Suspected engine defect, left failing: decision 6 of docs/decisions/durability-policy.md
        /// records the file of a failed write, but a promotion whose header write to the data file
        /// fails records none ($database.writeFailure.file is null, and the refusal omits "on the data
        /// file"): DiskService.WriteFileVersion does not name the file its write or sync failed in.
        /// </summary>
        [Fact]
        public void Failed_promotion_header_write_names_the_data_file()
        {
            using var data = new TornHeaderData();
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy })))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Auto })))
            {
                data.Armed = true;
                Action compact = () => db.GetCollection("compact").Insert(Compact());
                compact.Should().Throw<IOException>().WithMessage("File format promotion failed*");
                var file = db.GetCollection("$database").FindAll().Single()["writeFailure"]["file"];
                (file.IsNull ? null : file.AsString).Should().Be("data", "the header write to the data file failed");
            }
        }

        private static BsonDocument[] Compact() => Enumerable.Range(1, 10).Select(i =>
        {
            var doc = new BsonDocument { ["_id"] = i };
            for (var f = 0; f < 12; f++) doc["aVeryLongFieldNameNumber" + f] = f;
            return doc;
        }).ToArray();

        /// <summary>
        /// Tears the next 8 KiB write at position 0 (the data header) after its first 40 bytes (the
        /// new checksum lands, the new version byte does not) and throws a non-I/O exception.
        /// </summary>
        private sealed class TornHeaderData : MemoryStream
        {
            internal bool Armed, Torn;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == 0 && count == Constants.PAGE_SIZE)
                {
                    Armed = false;
                    base.Write(buffer, offset, 40);
                    Torn = true;
                    throw new UnauthorizedAccessException("injected torn header write");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
