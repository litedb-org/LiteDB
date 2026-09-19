using System;
using System.IO;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public partial class Issue1940_RecoverySafety_Tests
    {
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
    }
}
