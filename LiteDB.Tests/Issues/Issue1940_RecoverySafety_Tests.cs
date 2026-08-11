using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1940_RecoverySafety_Tests
    {
        private const int PageSize = Constants.PAGE_SIZE;
        private const int PageIdOffset = 0;
        private const int PageTypeOffset = 4;
        private const int NextPageIdOffset = 9;
        private const int TransactionIdOffset = 14;
        private const int IsConfirmedOffset = 18;
        private const int FreeEmptyPageListOffset = 60;
        private const int CheckpointOffset = 97;
        private const int LimitSizeOffset = 101;

        [Fact]
        public void Healing_must_not_commit_pages_from_an_abandoned_WAL_transaction()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var databasePath = this.DatabasePath(tempDirectory);
                var logPath = this.LogPath(tempDirectory);

                // The fixture contains abandoned transaction 24 pages, followed by a
                // committed transaction 20. Model one later transaction 23 commit.
                // Recovery must choose an ID other than the already-present transaction 24.
                this.AppendConfirmedCopyOfLastWalHeader(logPath, 23);

                using (var db = new LiteDatabase(databasePath))
                {
                    db.Checkpoint();
                }

                var data = File.ReadAllBytes(databasePath);

                using (new AssertionScope())
                {
                    this.ReadPageType(data, 10).Should().Be(PageType.Data,
                        "the abandoned transaction never committed page 10 as Empty");
                    this.ReadPageType(data, 13).Should().Be(PageType.Data,
                        "the abandoned transaction never committed page 13 as Empty");
                    this.ReadPageType(data, 14).Should().Be(PageType.Data,
                        "the abandoned transaction never committed page 14 as Empty");
                }
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        [Fact]
        public void A_failed_repair_write_must_be_reported_instead_of_retried_past_a_torn_WAL_page()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var dataBytes = File.ReadAllBytes(this.DatabasePath(tempDirectory));
                var logBytes = File.ReadAllBytes(this.LogPath(tempDirectory));

                // Keep the WAL on close so the next open sees exactly what recovery wrote.
                this.SetCheckpointToZeroInConfirmedHeaders(logBytes);

                var data = ExpandableStream(dataBytes);
                var log = new PartialWriteOnceStream(logBytes);
                LiteEngine firstOpen = null;
                Exception reopenException = null;

                var firstOpenException = Record.Exception(() => firstOpen = new LiteEngine(new EngineSettings
                {
                    DataStream = data,
                    LogStream = log
                }));

                if (firstOpen != null)
                {
                    firstOpen.Close(new Exception("simulate crash before checkpoint"));
                }

                var damagedData = data.ToArray();
                var damagedLog = log.ToArray();
                reopenException = Record.Exception(() =>
                {
                    var reopened = new LiteEngine(new EngineSettings
                    {
                        DataStream = ExpandableStream(damagedData),
                        LogStream = ExpandableStream(damagedLog)
                    });

                    reopened.Close(new Exception("inspection only"));
                });

                using (new AssertionScope())
                {
                    reopenException.Should().BeNull(
                        "a swallowed repair error must not turn the next open into 'invalid database'");
                    log.WriteCalls.Should().BeLessOrEqualTo(1,
                        "open may defer the repair, but must never retry a failed append past a torn WAL page");

                    if (log.WriteCalls == 0)
                    {
                        firstOpenException.Should().BeNull("a deferred repair performs no fallible WAL write during open");
                    }
                    else
                    {
                        firstOpenException.Should().BeOfType<IOException>(
                            "the caller must be told when an eager repair could not be persisted");
                    }
                }
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        [Fact]
        public void A_transient_read_error_must_not_permanently_discard_a_healthy_free_list()
        {
            var initialData = new MemoryStream();
            var initialLog = new MemoryStream();

            using (var db = new LiteDatabase(initialData, logStream: initialLog))
            {
                db.CheckpointSize = 0;
                var collection = db.GetCollection("items");

                for (var i = 0; i < 100; i++)
                {
                    collection.Insert(new BsonDocument
                    {
                        ["_id"] = i,
                        ["payload"] = new string('x', 4000)
                    });
                }

                db.DropCollection("items");
            }

            var dataBytes = initialData.ToArray();
            var logBytes = initialLog.ToArray();
            var freeListBefore = LatestConfirmedHeaderFreeList(logBytes);
            freeListBefore.Should().NotBe(uint.MaxValue, "the setup deliberately creates a healthy reusable-page list");

            // RestoreIndex reads this page once. If startup validation reads the
            // same free-list page again, fail that exact second read.
            var freeListPageOffset = this.FindLatestConfirmedPage(logBytes, freeListBefore);
            var faultyLog = new ReadFaultOnceStream(logBytes, freeListPageOffset, 2);
            LiteEngine opened = null;
            var openException = Record.Exception(() => opened = new LiteEngine(new EngineSettings
            {
                DataStream = ExpandableStream(dataBytes),
                LogStream = faultyLog
            }));

            opened?.Close(new Exception("inspection only"));

            using (new AssertionScope())
            {
                LatestConfirmedHeaderFreeList(faultyLog.ToArray()).Should().Be(freeListBefore,
                    "an IOException says nothing about whether the on-disk free list is corrupt");
                if (faultyLog.FaultInjected)
                {
                    openException.Should().BeOfType<IOException>(
                        "an operational read failure must be reported without changing database metadata");
                }
                else
                {
                    openException.Should().BeNull(
                        "lazy validation need not reread the free-list page during open");
                }
            }
        }

        [Fact]
        public void Upgrade_must_heal_corruption_already_checkpointed_into_the_data_file()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var databasePath = this.DatabasePath(tempDirectory);
                var logPath = this.LogPath(tempDirectory);

                // This is exactly what an older LiteDB checkpoint does: copy every page
                // from a confirmed WAL transaction into the data file, then remove the WAL.
                this.CheckpointFixtureWithoutHealing(databasePath, logPath);
                File.Exists(logPath).Should().BeFalse();

                Action openAndAllocateAPage = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    db.GetCollection("after_upgrade").Insert(new BsonDocument { ["_id"] = 1 });
                };

                openAndAllocateAPage.Should().NotThrow(
                    "legacy corruption remains recoverable even when an old checkpoint removed the WAL");
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        [Fact]
        public void Healing_a_bad_tail_must_keep_the_valid_reusable_pages_before_it()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var databasePath = this.DatabasePath(tempDirectory);
                var logPath = this.LogPath(tempDirectory);
                var logBytes = File.ReadAllBytes(logPath);

                var latestHeader = this.FindLatestConfirmedPage(logBytes, 0);
                var latestPage9 = this.FindLatestConfirmedPage(logBytes, 9);

                // Build a clear chain: 16 -> 11 -> 12 -> 9 -> 13.
                // The first four pages are valid Empty pages; page 13 is a Data page.
                WriteUInt32(logBytes, latestHeader + FreeEmptyPageListOffset, 16);
                WriteInt64(logBytes, latestHeader + LimitSizeOffset, 18L * PageSize);
                WriteUInt32(logBytes, latestPage9 + NextPageIdOffset, 13);
                File.WriteAllBytes(logPath, logBytes);

                Action insertUsingTheValidFreePages = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    db.GetCollection("fits_in_the_free_pages").Insert(new BsonDocument { ["_id"] = 1 });
                };

                insertUsingTheValidFreePages.Should().NotThrow(
                    "pages 16, 11, 12 and 9 are reusable, so the fixed 18-page database has enough room");
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        [Fact]
        public void A_tiny_WAL_must_not_read_the_entire_healthy_free_list_during_open()
        {
            var data = new MemoryStream();
            var log = new MemoryStream();

            // First put many reusable pages in the data file.
            using (var db = new LiteDatabase(data, logStream: log))
            {
                var collection = db.GetCollection("large_deleted_collection");

                for (var i = 0; i < 100; i++)
                {
                    collection.Insert(new BsonDocument
                    {
                        ["_id"] = i,
                        ["payload"] = new string('x', 4000)
                    });
                }

                db.DropCollection("large_deleted_collection");
                db.Checkpoint();
            }

            var checkpointedData = data.ToArray();

            // Then add one tiny transaction to the WAL without checkpointing it.
            var tinyWal = new MemoryStream();
            using (var db = new LiteDatabase(ExpandableStream(checkpointedData), logStream: tinyWal))
            {
                db.CheckpointSize = 0;
                db.GetCollection("marker").Insert(new BsonDocument { ["_id"] = 1 });
            }

            var tinyWalBytes = tinyWal.ToArray();
            tinyWalBytes.Length.Should().BeLessThan(10 * PageSize, "the setup uses only a tiny WAL");

            var baselineData = new CountingReadStream(checkpointedData);
            var baseline = new LiteEngine(new EngineSettings
            {
                DataStream = baselineData,
                LogStream = new MemoryStream()
            });
            baseline.Close(new Exception("measurement only"));

            var walData = new CountingReadStream(checkpointedData);
            var withTinyWal = new LiteEngine(new EngineSettings
            {
                DataStream = walData,
                LogStream = ExpandableStream(tinyWalBytes)
            });
            withTinyWal.Close(new Exception("measurement only"));

            var extraDataPageReads = walData.FullPageReads - baselineData.FullPageReads;
            extraDataPageReads.Should().BeLessOrEqualTo(20,
                "opening a tiny WAL should do bounded work instead of scanning every deleted page");
        }

        private void AppendConfirmedCopyOfLastWalHeader(string logPath, uint transactionId)
        {
            var log = File.ReadAllBytes(logPath);
            var header = new byte[PageSize];

            Buffer.BlockCopy(log, log.Length - PageSize, header, 0, PageSize);
            ((PageType)header[PageTypeOffset]).Should().Be(PageType.Header);
            WriteUInt32(header, TransactionIdOffset, transactionId);
            header[IsConfirmedOffset] = 1;

            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(header, 0, header.Length);
        }

        private PageType ReadPageType(byte[] data, uint pageId)
        {
            return (PageType)data[checked((int)(pageId * PageSize)) + PageTypeOffset];
        }

        private void CheckpointFixtureWithoutHealing(string databasePath, string logPath)
        {
            var log = File.ReadAllBytes(logPath);
            var confirmedTransactions = this.FindConfirmedTransactions(log);

            using (var data = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
                {
                    var transactionId = ReadUInt32(log, offset + TransactionIdOffset);
                    if (confirmedTransactions.Contains(transactionId) == false)
                    {
                        continue;
                    }

                    var page = new byte[PageSize];
                    Buffer.BlockCopy(log, offset, page, 0, PageSize);
                    WriteUInt32(page, TransactionIdOffset, uint.MaxValue);
                    page[IsConfirmedOffset] = 0;

                    var pageId = ReadUInt32(page, PageIdOffset);
                    data.Position = pageId * (long)PageSize;
                    data.Write(page, 0, page.Length);
                }
            }

            File.Delete(logPath);
        }

        private HashSet<uint> FindConfirmedTransactions(byte[] log)
        {
            var result = new HashSet<uint>();

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + IsConfirmedOffset] != 0)
                {
                    result.Add(ReadUInt32(log, offset + TransactionIdOffset));
                }
            }

            return result;
        }

        private int FindLatestConfirmedPage(byte[] log, uint pageId)
        {
            var confirmedTransactions = this.FindConfirmedTransactions(log);
            var result = -1;

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (ReadUInt32(log, offset + PageIdOffset) == pageId &&
                    confirmedTransactions.Contains(ReadUInt32(log, offset + TransactionIdOffset)))
                {
                    result = offset;
                }
            }

            result.Should().BeGreaterOrEqualTo(0, $"the fixture must contain confirmed page {pageId}");
            return result;
        }

        private void SetCheckpointToZeroInConfirmedHeaders(byte[] log)
        {
            var confirmedTransactions = this.FindConfirmedTransactions(log);

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + PageTypeOffset] == (byte)PageType.Header &&
                    confirmedTransactions.Contains(ReadUInt32(log, offset + TransactionIdOffset)))
                {
                    WriteUInt32(log, offset + CheckpointOffset, 0);
                }
            }
        }

        private static uint LatestConfirmedHeaderFreeList(byte[] log)
        {
            var result = uint.MaxValue;

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + PageTypeOffset] == (byte)PageType.Header &&
                    log[offset + IsConfirmedOffset] != 0)
                {
                    result = ReadUInt32(log, offset + FreeEmptyPageListOffset);
                }
            }

            return result;
        }

        private string ExtractFixture()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"litedb-issue1940-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            ZipFile.ExtractToDirectory(
                Path.Combine(AppContext.BaseDirectory, "Resources", "Issue1940_CorruptFreeEmptyList.zip"),
                tempDirectory);

            File.Exists(this.DatabasePath(tempDirectory)).Should().BeTrue();
            File.Exists(this.LogPath(tempDirectory)).Should().BeTrue();

            return tempDirectory;
        }

        private string DatabasePath(string tempDirectory)
        {
            return Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList.db");
        }

        private string LogPath(string tempDirectory)
        {
            return Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList-log.db");
        }

        private void DeleteTempDirectory(string tempDirectory)
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                bytes[offset + 1] << 8 |
                bytes[offset + 2] << 16 |
                bytes[offset + 3] << 24);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(byte[] bytes, int offset, long value)
        {
            var encoded = unchecked((ulong)value);

            for (var index = 0; index < sizeof(long); index++)
            {
                bytes[offset + index] = (byte)(encoded >> (index * 8));
            }
        }

        private static MemoryStream ExpandableStream(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private sealed class PartialWriteOnceStream : MemoryStream
        {
            private bool _failed;

            public PartialWriteOnceStream(byte[] bytes)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
            }

            public int WriteCalls { get; private set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                this.WriteCalls++;

                if (this._failed == false && count == PageSize)
                {
                    this._failed = true;
                    base.Write(buffer, offset, 32);
                    throw new IOException("injected torn WAL page");
                }

                base.Write(buffer, offset, count);
            }
        }

        private sealed class ReadFaultOnceStream : MemoryStream
        {
            private readonly long _targetPosition;
            private readonly int _targetVisit;
            private int _targetVisits;
            private bool _failed;

            public ReadFaultOnceStream(byte[] bytes, long targetPosition, int targetVisit)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
                this._targetPosition = targetPosition;
                this._targetVisit = targetVisit;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                this.ReadCalls++;

                if (count == PageSize && this.Position == this._targetPosition)
                {
                    this._targetVisits++;
                }

                if (this._failed == false && this._targetVisits == this._targetVisit)
                {
                    this._failed = true;
                    throw new IOException("injected transient read failure");
                }

                return base.Read(buffer, offset, count);
            }

            public int ReadCalls { get; private set; }
            public bool FaultInjected => this._failed;
        }

        private sealed class CountingReadStream : MemoryStream
        {
            public CountingReadStream(byte[] bytes)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
            }

            public int FullPageReads { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var result = base.Read(buffer, offset, count);

                if (count == PageSize && result == PageSize)
                {
                    this.FullPageReads++;
                }

                return result;
            }
        }
    }
}
