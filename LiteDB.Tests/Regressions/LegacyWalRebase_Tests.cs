using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The legacy WAL has no frame checksum, so a transaction that began before a newer transaction
    /// reached the log must first append a new-ID anchor and then rewrite its earlier slots in place.
    /// A torn rewrite must stop the writer before any rollback or later transaction can append behind it.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyWalRebase_Tests
    {
        [Fact]
        public void Torn_legacy_transaction_id_rewrite_stops_before_followup_wal_and_keeps_confirmed_data()
        {
            using var data = SeedLegacyData();
            using var log = new FailingLegacyLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var state = new EngineState(null, settings);
            byte[] crashData;
            byte[] crashLog;
            using (var disk = new DiskService(settings, state, new[] { 2 }))
            {
                var old = new TransactionPages { TransactionID = 5 };
                var positions = new Dictionary<uint, PagePosition>();
                disk.WriteLogDisk(Pages(disk, 1, 5, confirmed: false), (id, position) =>
                {
                    positions[id] = new PagePosition(id, position);
                }, old.DirtyPages);

                // The lower-ID transaction has already dirtied page 1. A newer transaction reached
                // the physical tail first, so its first frame forces the legacy rebase path.
                var current = new TransactionPages { TransactionID = 1 };
                current.DirtyPages[1] = positions[1];
                log.Armed = true;
                Action failed = () => disk.WriteLogDisk(Pages(disk, 1, 1, confirmed: true), null, current, () => 6);
                failed.Should().Throw<IOException>().WithMessage("injected legacy ID rewrite");
                log.Overwrites.Should().Be(1, "the injector must tear the earlier slot rewrite");
                state.Stopped.Should().BeTrue();

                crashData = data.ToArray();
                crashLog = log.ToArray();
                var length = log.Length;
                Action followup = () => disk.WriteLogDisk(Pages(disk, 2, 7, confirmed: false));
                followup.Should().Throw<IOException>();
                log.Length.Should().Be(length, "a stopped engine cannot append behind the torn slot");
            }

            using var recoveredData = Copy(crashData);
            using var recoveredLog = Copy(crashLog);
            using var recovered = new LiteEngine(new EngineSettings
            {
                DataStream = recoveredData, LogStream = recoveredLog, ReadOnly = true, LegacyIndexScan = true
            });
            using var db = new LiteDatabase(recovered, disposeOnClose: false);
            db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(7,
                "recovery must retain the confirmed data file and ignore the unconfirmed torn transaction");
        }

        [Fact]
        public void Failed_flush_during_legacy_rebase_stops_before_followup_wal()
        {
            using var data = SeedLegacyData();
            using var log = new FailingLegacyLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var state = new EngineState(null, settings);
            byte[] crashData;
            byte[] crashLog;
            using (var disk = new DiskService(settings, state, new[] { 2 }))
            {
                var old = new TransactionPages { TransactionID = 5 };
                var positions = new Dictionary<uint, PagePosition>();
                disk.WriteLogDisk(Pages(disk, 1, 5, confirmed: false), (id, position) =>
                {
                    positions[id] = new PagePosition(id, position);
                }, old.DirtyPages);

                var current = new TransactionPages { TransactionID = 1 };
                current.DirtyPages[1] = positions[1];
                log.FailFlush = true;
                Action failed = () => disk.WriteLogDisk(Pages(disk, 1, 1, confirmed: true), null, current, () => 6);
                failed.Should().Throw<IOException>().WithMessage("*flush*");
                state.Stopped.Should().BeTrue("a failed flush after the legacy anchor leaves the WAL uncertain");

                crashData = data.ToArray();
                crashLog = log.ToArray();
                var length = log.Length;
                Action followup = () => disk.WriteLogDisk(Pages(disk, 2, 7, confirmed: false));
                followup.Should().Throw<IOException>();
                log.Length.Should().Be(length, "a stopped engine cannot append behind a failed rebase flush");
            }

            using var recoveredData = Copy(crashData);
            using var recoveredLog = Copy(crashLog);
            using var recovered = new LiteEngine(new EngineSettings
            {
                DataStream = recoveredData, LogStream = recoveredLog, ReadOnly = true, LegacyIndexScan = true
            });
            using var db = new LiteDatabase(recovered, disposeOnClose: false);
            db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(7,
                "recovery must retain the data file while ignoring the unconfirmed rebase anchor");
        }

        private static MemoryStream SeedLegacyData()
        {
            var data = new MemoryStream();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
                db.Checkpoint();
            }
            ChecksumTestFiles.MakeLegacy(data, new MemoryStream(), null, 8);
            data.Position = 0;
            return data;
        }

        private static IEnumerable<PageBuffer> Pages(DiskService disk, uint id, uint transactionID, bool confirmed)
        {
            var page = disk.NewPage();
            page.Write(id, BasePage.P_PAGE_ID);
            page.Write(transactionID, BasePage.P_TRANSACTION_ID);
            page.Write((byte)PageType.Data, BasePage.P_PAGE_TYPE);
            page.Write(confirmed, BasePage.P_IS_CONFIRMED);
            yield return page;
        }

        private static MemoryStream Copy(byte[] bytes)
        {
            var copy = new MemoryStream();
            copy.Write(bytes, 0, bytes.Length);
            copy.Position = 0;
            return copy;
        }

        private sealed class FailingLegacyLog : MemoryStream
        {
            internal bool Armed;
            internal bool FailFlush;
            internal int Overwrites;
            private int _writes;

            public override void Write(byte[] buffer, int offset, int count)
            {
                _writes++;
                // The first write is the old transaction. The second is the new-ID anchor. The
                // third is RewriteLogTransactionIDs, the in-place write under test.
                if (Armed && _writes >= 3 && Position < Length)
                {
                    Armed = false;
                    Overwrites++;
                    base.Write(buffer, offset, Math.Min(count, 127));
                    throw new IOException("injected legacy ID rewrite");
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (FailFlush) throw new IOException("injected legacy rebase flush failure");
                base.Flush();
            }
        }
    }
}
