using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;
using static LiteDB.Tests.Engine.WalIdentityCrash_Tests;

namespace LiteDB.Tests.Engine
{
    public class WalIdentityCompatibility_Tests
    {
        [Fact]
        public void Legacy_data_payload_matching_identity_magic_is_still_replayed()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var receipt = new string('a', 45) + "LDBWAL01" + new string('b', 147);
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["receipt"] = new string('a', 200) });
                db.Checkpoint();
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["receipt"] = receipt });
            }
            var legacyData = data.ToArray();
            Array.Clear(legacyData, WalIdentity.Position, WalIdentity.Size);
            var modernLog = log.ToArray();
            var legacyLog = new byte[modernLog.Length - PAGE_SIZE];
            Buffer.BlockCopy(modernLog, PAGE_SIZE, legacyLog, 0, legacyLog.Length);
            legacyLog[BasePage.P_PAGE_TYPE].Should().Be((byte)PageType.Data);
            System.Text.Encoding.ASCII.GetString(legacyLog, WalIdentity.Position, 8).Should().Be("LDBWAL01");
            using var restoredData = Copy(legacyData);
            using var restoredLog = Copy(legacyLog);
            using var reopened = Open(restoredData, restoredLog);
            reopened.GetCollection("rows").FindById(1)["receipt"].AsString.Should().Be(receipt);
            reopened.Checkpoint();
        }

        [Theory]
        [InlineData(BasePage.P_PAGE_ID, false)]
        [InlineData(BasePage.P_PAGE_ID, true)]
        [InlineData(BasePage.P_PAGE_TYPE, false)]
        [InlineData(BasePage.P_PAGE_TYPE, true)]
        [InlineData(BasePage.P_TRANSACTION_ID, false)]
        [InlineData(BasePage.P_TRANSACTION_ID, true)]
        [InlineData(BasePage.P_IS_CONFIRMED, false)]
        [InlineData(BasePage.P_IS_CONFIRMED, true)]
        public void Damaged_modern_prefix_cannot_downgrade_to_legacy_replay(int field, bool damageMagic)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] legacyData;
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                db.Checkpoint();
                legacyData = data.ToArray();
                Array.Clear(legacyData, WalIdentity.Position, WalIdentity.Size);
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["receipt"] = "foreign!!" });
            }
            var damagedLog = log.ToArray();
            for (var offset = PAGE_SIZE; offset < damagedLog.Length; offset += PAGE_SIZE)
                damagedLog[offset + BasePage.P_PAGE_TYPE].Should().NotBe((byte)PageType.Header,
                    "the fixture must have no header frame to fall back on for identity validation");
            damagedLog[field] ^= 1;
            if (damageMagic) damagedLog[WalIdentity.Position] ^= 1;
            using var victimData = Copy(legacyData);
            using var victimLog = Copy(damagedLog);
            Action open = () => { using var db = Open(victimData, victimLog); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            victimData.ToArray().Should().Equal(legacyData);
            victimLog.ToArray().Should().Equal(damagedLog);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Readonly_caller_streams_cannot_write_or_checkpoint_a_matching_WAL(bool checkpoint)
        {
            var image = CreateImage(legacy: false);
            using var data = Copy(image.Data);
            using var log = Copy(image.Log);
            using (var db = Open(data, log, readOnly: true))
            {
                AssertRows(db, 1);
                Action write = () =>
                {
                    if (checkpoint) db.Checkpoint();
                    else db.GetCollection("rows").Insert(Row(2));
                };
                write.Should().Throw<NotSupportedException>();
            }
            data.ToArray().Should().Equal(image.Data);
            log.ToArray().Should().Equal(image.Log);
        }

        [Fact]
        public void Legacy_matching_WAL_replays_and_gains_identity_after_checkpoint()
        {
            var image = CreateImage(legacy: true);
            using var data = Copy(image.Data);
            using var log = Copy(image.Log);
            using (var db = Open(data, log))
            {
                AssertRows(db, 1);
                Assert.Null(WalIdentity.Read(WalIdentity.ReadHeader(data)));
                db.Checkpoint();
                Assert.NotNull(WalIdentity.Read(WalIdentity.ReadHeader(data)));
                log.Length.Should().Be(0);
                db.GetCollection("rows").Insert(Row(2));
            }
            using (var db = Open(data, log)) AssertRows(db, 1, 2);
        }

        [Fact]
        public void Failed_legacy_truncate_keeps_an_unmarked_recoverable_generation()
        {
            var image = CreateImage(legacy: true);
            using var data = new CrashStream();
            using var log = new CrashStream();
            data.Write(image.Data, 0, image.Data.Length);
            log.Write(image.Log, 0, image.Log.Length);
            data.Flush();
            log.Flush();
            using (var db = Open(data, log))
            {
                log.FailTruncate = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>();
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            Assert.Null(WalIdentity.Read(WalIdentity.ReadHeader(recoveredData)));
            using (var db = Open(recoveredData, recoveredLog)) AssertRows(db, 1);
        }

        [Fact]
        public void Recovery_reader_rejects_a_foreign_WAL_instead_of_importing_it()
        {
            var own = CreateImage(legacy: false);
            var foreign = CreateImage(legacy: false);
            using var data = Copy(own.Data);
            using var log = Copy(foreign.Log);
            using var reader = new FileReaderV8(new EngineSettings { DataStream = data, LogStream = log }, new List<FileReaderError>());
            Action open = () => reader.Open();
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(own.Data);
            log.ToArray().Should().Equal(foreign.Log);
        }

        [Fact]
        public void Protected_file_rejects_a_prefixless_WAL_before_alignment_repairs()
        {
            var image = CreateImage(legacy: false);
            using var data = Copy(image.Data);
            using var log = new MemoryStream();
            log.Write(image.Log, PAGE_SIZE, image.Log.Length - PAGE_SIZE);
            log.WriteByte(73); // Even an unaligned foreign tail must be retained.
            var before = log.ToArray();
            Action open = () => { using var db = Open(data, log); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(image.Data);
            log.ToArray().Should().Equal(before);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Torn_identity_metadata_is_rejected_without_modifying_either_file(bool corruptLog)
        {
            var image = CreateImage(legacy: false);
            (corruptLog ? image.Log : image.Data)[WalIdentity.Position + 32] ^= 1;
            using var data = Copy(image.Data);
            using var log = Copy(image.Log);
            Action open = () => { using var db = Open(data, log); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(image.Data);
            log.ToArray().Should().Equal(image.Log);
        }

        [Fact]
        public void Legacy_header_creation_time_mismatch_is_rejected_without_repair()
        {
            var image = CreateImage(legacy: true);
            var wrongTime = BitConverter.GetBytes(BitConverter.ToInt64(image.Data, HeaderPage.P_CREATION_TIME) + TimeSpan.TicksPerDay);
            for (var offset = 0; offset < image.Log.Length; offset += PAGE_SIZE)
            {
                if (image.Log[offset + BasePage.P_PAGE_TYPE] == (byte)PageType.Header)
                    Buffer.BlockCopy(wrongTime, 0, image.Log, offset + HeaderPage.P_CREATION_TIME, 8);
            }
            using var data = Copy(image.Data);
            using var log = Copy(image.Log);
            Action open = () => { using var db = Open(data, log); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(image.Data);
            log.ToArray().Should().Equal(image.Log);
        }

        [Fact]
        public void Recovery_and_checkpoint_ignore_prefix_when_transaction_ID_wraps_to_zero()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
            using (var db = new LiteDatabase(engine))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var wal = typeof(LiteEngine).GetField("_walIndex", flags).GetValue(engine);
                typeof(WalIndexService).GetField("_lastTransactionID", flags).SetValue(wal, -1);
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["receipt"] = "updated" });
            }
            using (var reader = new FileReaderV8(new EngineSettings { DataStream = data, LogStream = log }, new List<FileReaderError>()))
            {
                reader.Open();
                reader.GetCollections().Should().Contain("rows");
                reader.GetDocuments("rows").Should().ContainSingle().Which["receipt"].AsString.Should().Be("updated");
            }
            using (var db = Open(data, log)) db.Checkpoint();
            using (var db = Open(data, log))
                db.GetCollection("rows").FindById(1)["receipt"].AsString.Should().Be("updated");
        }

        private static (byte[] Data, byte[] Log) CreateImage(bool legacy)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
            }
            var dataBytes = data.ToArray();
            var logBytes = log.ToArray();
            if (legacy)
            {
                // Model the old v8 layout: no prefix and zero reserved metadata.
                Array.Clear(dataBytes, WalIdentity.Position, WalIdentity.Size);
                var frames = new byte[logBytes.Length - PAGE_SIZE];
                Buffer.BlockCopy(logBytes, PAGE_SIZE, frames, 0, frames.Length);
                logBytes = frames;
                for (var offset = 0; offset < logBytes.Length; offset += PAGE_SIZE)
                {
                    if (logBytes[offset + BasePage.P_PAGE_TYPE] == (byte)PageType.Header)
                        Array.Clear(logBytes, offset + WalIdentity.Position, WalIdentity.Size);
                }
            }
            return (dataBytes, logBytes);
        }
    }
}
