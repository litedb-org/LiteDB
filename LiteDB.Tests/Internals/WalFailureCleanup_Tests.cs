using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalFailureCleanup_Tests
    {
        private const int FailHeaderWrite = 0;
        private const int FailLengthRead = -1;

        [Theory]
        [InlineData(null, 1, 1000)]
        [InlineData(null, 3, 1000)]
        [InlineData(null, FailHeaderWrite, 1000)]
        [InlineData(null, FailLengthRead, 1000)]
        [InlineData(null, 1, 4)]
        [InlineData(null, 3, 4)]
        [InlineData("secret", 1, 1000)]
        [InlineData("secret", 3, 1000)]
        [InlineData("secret", FailHeaderWrite, 1000)]
        [InlineData("secret", FailLengthRead, 1000)]
        [InlineData("secret", 1, 4)]
        [InlineData("secret", 3, 4)]
        public void FailedWrite_ReleasesAllFrames_AndPreservesCommittedData(string password, int failAt, int transactionPages)
        {
            FailWrite(password, failAt, transactionPages);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FailWrite(string password, int failAt, int transactionPages)
        {
            using var data = new MemoryStream();
            using var log = new FailingLogStream();
            var settings = new EngineSettings
            {
                DataStream = data,
                LogStream = log,
                Password = password,
                CacheSize = 1024 * 1024,
                TransactionPageLimit = transactionPages
            };
            using var engine = new LiteEngine(settings);
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = 0 });
            database.Checkpoint();
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out _);
            var cache = transaction.CreateSnapshot(LockMode.Read, "docs", false).CollectionPage.Buffer.Cache;
            monitor.ReleaseTransaction(transaction);

            log.WritesUntilFailure = failAt;
            if (failAt == FailHeaderWrite)
            {
                engine.SimulateDiskWriteFail = page =>
                {
                    if (page.ReadUInt32(BasePage.P_PAGE_ID) == 0)
                        throw new IOException("injected WAL I/O failure");
                };
            }
            Action insert = () => collection.Insert(Enumerable.Range(1, 20).Select(i =>
                new BsonDocument { ["_id"] = i, ["payload"] = new string('x', 6000) }));
            insert.Should().Throw<IOException>().WithMessage("injected WAL I/O failure");

            cache.WritablePages.Should().Be(0);
            cache.PinnedPages.Should().Be(0);
            cache.LoadingPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            cache.TotalPages.Should().Be(0, "error-close must release the cache before finalization");
            data.CanRead.Should().BeTrue();
            log.CanRead.Should().BeTrue();

            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            var documents = reopened.GetCollection("docs").FindAll().ToArray();
            documents.Should().ContainSingle().Which["_id"].AsInt32.Should().Be(0);
        }

        [Fact]
        public void Dispose_WithOneStaleLease_ReleasesOtherWritableFrames()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 0 });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, false, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var collection = snapshot.CollectionPage;
            var cache = collection.Buffer.Cache;
            snapshot.GetPage<BasePage>(collection.GetCollectionIndex("_id").Head.PageID);
            cache.WritablePages.Should().BeGreaterThan(1);
            cache.DiscardPage(collection.Buffer); // Deliberately invalidate just this lease.

            Action dispose = transaction.Dispose;
            dispose.Should().Throw<AggregateException>();
            cache.WritablePages.Should().Be(0, "one stale lease must not prevent cleanup of other pages");
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            monitor.ReleaseTransaction(transaction);
        }

        private sealed class FailingLogStream : MemoryStream
        {
            public int WritesUntilFailure { get; set; }

            public override long Length
            {
                get
                {
                    if (this.WritesUntilFailure == FailLengthRead)
                    {
                        this.WritesUntilFailure = 0;
                        throw new IOException("injected WAL I/O failure");
                    }
                    return base.Length;
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.WritesUntilFailure > 0 && --this.WritesUntilFailure == 0)
                {
                    // Exercise rollback after a real partial stream write.
                    base.Write(buffer, offset, Math.Min(count, 127));
                    throw new IOException("injected WAL I/O failure");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
