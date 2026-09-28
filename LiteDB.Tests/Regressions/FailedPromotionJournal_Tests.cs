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
    /// and a failed append never truncates an outstanding journal.
    /// </summary>
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
                compact.Should().Throw<IOException>("the promotion stops the engine with the header journal outstanding")
                    .WithMessage("File format promotion failed*").WithInnerException<UnauthorizedAccessException>();
                data.Torn.Should().BeTrue();
                Action later = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 6 });
                later.Should().Throw<IOException>().WithMessage("Engine closed*");
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
