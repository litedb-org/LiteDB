using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using Xunit.Abstractions;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalSlotReuse_Tests
    {
        private readonly ITestOutputHelper _output;

        public WalSlotReuse_Tests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void DeleteMany_WithFrequentSafepoints_BoundsPeakLogByDistinctPages()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new FileStream(dataFile.Filename, FileMode.Create, FileAccess.ReadWrite);
            using var log = new PeakLogStream(logFile.Filename);
            var settings = new EngineSettings
            {
                DataStream = data,
                LogStream = log,
                TransactionPageLimit = 32,
                CacheSize = 1024 * 1024
            };
            using (var engine = new LiteEngine(settings))
            using (var database = new LiteDatabase(engine, disposeOnClose: false))
            {
                database.Pragma(Pragmas.CHECKPOINT, 0);
                var collection = database.GetCollection("docs");
                collection.Insert(CreateDocuments(50000));
                database.Checkpoint();
                log.PeakLength = log.Length;
                var dataLength = data.Length;

                collection.DeleteMany("$.remove = true").Should().Be(25000);

                _output.WriteLine($"Data: {dataLength} bytes; peak WAL: {log.PeakLength} bytes ({(double)log.PeakLength / dataLength:F2}x)");
                log.PeakLength.Should().BeLessThanOrEqualTo(dataLength * 3 / 2);
                collection.Count().Should().Be(25000);
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().HaveCount(25000)
                .And.OnlyContain(document => !document["remove"].AsBoolean);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void RewrittenPages_PreserveCommitRollbackAndRecovery(string password, bool rollback)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 2
            };
            using (var engine = new LiteEngine(settings))
            using (var database = new LiteDatabase(engine, disposeOnClose: false))
            {
                database.Pragma(Pragmas.CHECKPOINT, 0);
                var collection = database.GetCollection("docs");
                collection.Insert(Enumerable.Range(0, 20).Select(id => new BsonDocument
                {
                    ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 4000)
                }));
                database.Checkpoint();
                database.BeginTrans();
                for (var value = 1; value <= 3; value++)
                {
                    for (var id = 0; id < 20; id++)
                    {
                        var document = collection.FindById(id);
                        document["value"] = value;
                        collection.Update(document);
                    }
                }
                if (rollback) database.Rollback();
                else database.Commit();
                collection.FindAll().Should().OnlyContain(document => document["value"].AsInt32 == (rollback ? 0 : 3));
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().HaveCount(20)
                .And.OnlyContain(document => document["value"].AsInt32 == (rollback ? 0 : 3));
            reopened.Checkpoint();
        }

        [Fact]
        public void WriteLogDisk_ReusesOnlyUnconfirmedSlots_AndRefreshesCachedBytes()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            using var disk = new DiskService(settings, new EngineState(null, settings), new[] { 10 });
            using var reader = disk.GetReader();
            var positions = new Dictionary<uint, PagePosition>();

            void Write(uint id, int value, bool confirmed = false)
            {
                var page = disk.NewPage();
                page.Write(id, BasePage.P_PAGE_ID);
                page.Write(confirmed, BasePage.P_IS_CONFIRMED);
                page.Write(value, PAGE_SIZE - sizeof(int));
                disk.WriteLogDisk(new[] { page }, (pageID, position) =>
                    positions[pageID] = new PagePosition(pageID, position), positions);
            }

            Write(1, 10);
            Write(2, 20);
            Write(1, 30);
            log.Length.Should().Be(2 * PAGE_SIZE);
            positions[1].Position.Should().Be(0);
            var updated = reader.ReadPage(0, false, FileOrigin.Log);
            updated.ReadInt32(PAGE_SIZE - sizeof(int)).Should().Be(30);
            updated.Release();

            Write(1, 40, confirmed: true);
            positions[1].Position.Should().Be(2 * PAGE_SIZE, "confirmation must follow every transaction page");
            log.Length.Should().Be(3 * PAGE_SIZE);
            disk.Cache.PinnedPages.Should().Be(0);
            disk.Cache.WritablePages.Should().Be(0);
            disk.Cache.LostFrames.Should().Be(0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedOverwrite_DoesNotConfirmEarlierSafepoints(string password)
        {
            using var data = new MemoryStream();
            using var log = new PartialWriteStream();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 2
            };
            using (var engine = new LiteEngine(settings))
            using (var database = new LiteDatabase(engine, disposeOnClose: false))
            {
                var collection = database.GetCollection("docs");
                collection.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                database.Checkpoint();
                database.BeginTrans();
                collection.Update(new BsonDocument { ["_id"] = 1, ["value"] = 1 });
                var transaction = engine.GetMonitor().GetThreadTransaction();
                transaction.Safepoint();
                transaction.Pages.DirtyPages.Should().NotBeEmpty();
                var cache = transaction.Snapshots.Single().CollectionPage.Buffer.Cache;
                collection.Update(new BsonDocument { ["_id"] = 1, ["value"] = 2 });
                var length = log.Length;

                log.FailNextWrite = true;
                Action update = () => collection.Update(new BsonDocument { ["_id"] = 1, ["value"] = 3 });
                update.Should().Throw<IOException>().WithMessage("injected partial overwrite");
                log.FailedPosition.Should().BeLessThan(length, "the injected failure must overwrite an existing slot");
                log.Length.Should().Be(length);
                cache.TotalPages.Should().Be(0);
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(0);
        }

        private sealed class PartialWriteStream : MemoryStream
        {
            public bool FailNextWrite { get; set; }
            public long FailedPosition { get; private set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.FailNextWrite)
                {
                    this.FailNextWrite = false;
                    this.FailedPosition = this.Position;
                    base.Write(buffer, offset, Math.Min(count, 127));
                    throw new IOException("injected partial overwrite");
                }
                base.Write(buffer, offset, count);
            }
        }

        private static IEnumerable<BsonDocument> CreateDocuments(int count)
        {
            var random = new Random(2587);
            var bytes = new byte[16];
            for (var i = 0; i < count; i++)
            {
                random.NextBytes(bytes);
                yield return new BsonDocument
                {
                    ["_id"] = new Guid(bytes), ["remove"] = i % 2 == 0, ["payload"] = new string('x', 128)
                };
            }
        }

        private sealed class PeakLogStream : FileStream
        {
            public long PeakLength { get; set; }

            public PeakLogStream(string filename) : base(filename, FileMode.Create, FileAccess.ReadWrite) { }

            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                this.PeakLength = Math.Max(this.PeakLength, this.Length);
            }
        }
    }
}
