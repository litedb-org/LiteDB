using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalChecksum_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void DamagedMiddleFrame_DiscardsWholeTransactionAndLaterCommits(string password, bool readOnly)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var end = checked((int)test.Log.Length);
            test.Update("docs", 2);
            test.Update("docs", 3);
            var original = test.Log.ToArray();
            var position = end + WalChecksum.FrameSize;
            foreach (var damage in new[] { "missing", "torn", "reordered", "removed", "partial-tail" })
            {
                var bytes = (byte[])original.Clone();
                if (damage == "missing") Array.Clear(bytes, position, WalChecksum.FrameSize);
                else if (damage == "torn") bytes[position + 400] ^= 0x10;
                else if (damage == "reordered")
                {
                    Buffer.BlockCopy(original, position + WalChecksum.FrameSize, bytes, position, WalChecksum.FrameSize);
                    Buffer.BlockCopy(original, position, bytes, position + WalChecksum.FrameSize, WalChecksum.FrameSize);
                }
                else if (damage == "removed")
                {
                    bytes = new byte[original.Length - WalChecksum.FrameSize];
                    Buffer.BlockCopy(original, 0, bytes, 0, position);
                    Buffer.BlockCopy(original, position + WalChecksum.FrameSize, bytes, position, bytes.Length - position);
                }
                else Array.Resize(ref bytes, position + 127);
                AssertRecovery(test.Data.ToArray(), bytes, password, readOnly, 1, end);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void StaleFramesFromPreviousGeneration_CannotVerify(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            var stale = test.Log.ToArray();
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var current = test.Log.ToArray();
            var preamble = password == null ? 0 : PAGE_SIZE;
            Buffer.BlockCopy(stale, preamble, current, preamble, WalChecksum.FrameSize);
            AssertRecovery(test.Data.ToArray(), current, password, false, 0, preamble);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void StaleReusedSlotsWithinGeneration_FailTransactionDigest(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var end = checked((int)test.Log.Length);
            test.Database.BeginTrans();
            test.Update("docs", 2);
            var transaction = test.Engine.GetMonitor().GetThreadTransaction();
            transaction.Safepoint();
            var stale = test.Log.ToArray();
            var positions = transaction.Pages.DirtyPages.Values.Select(x => x.Position).ToArray();
            test.Update("docs", 3);
            transaction.Safepoint();
            test.Database.Commit();
            test.Update("docs", 4);
            var current = test.Log.ToArray();
            foreach (var logical in positions)
            {
                var physical = checked((int)(logical / PAGE_SIZE * WalChecksum.FrameSize)) + (password == null ? 0 : PAGE_SIZE);
                Buffer.BlockCopy(stale, physical, current, physical, WalChecksum.FrameSize);
            }
            AssertRecovery(test.Data.ToArray(), current, password, false, 1, end);
        }

        [Fact]
        public void ConfirmationCount_IsValidatedEvenWhenItsFrameChecksumIsValid()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var end = checked((int)test.Log.Length);
            test.Update("docs", 2);
            var bytes = test.Log.ToArray();
            var frame = bytes.Length - WalChecksum.FrameSize;
            var metadata = new BufferSlice(bytes, frame + PAGE_SIZE, WalChecksum.MetadataSize);
            metadata.Write(metadata.ReadUInt32(32) + 1, 32);
            metadata.Write(0u, 4);
            metadata.Write(~Crc32C.Update(uint.MaxValue, bytes, frame, WalChecksum.FrameSize), 4);
            AssertRecovery(test.Data.ToArray(), bytes, null, false, 1, end);
        }

        [Fact]
        public void LostConfirmation_CannotAllowLaterCommitsThroughASequenceGap()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var end = checked((int)test.Log.Length);
            test.Update("docs", 2);
            var frame = checked((int)test.Log.Length) - WalChecksum.FrameSize;
            test.Update("docs", 3);
            var bytes = test.Log.ToArray();
            bytes[frame + BasePage.P_IS_CONFIRMED] = 0;
            var metadata = new BufferSlice(bytes, frame + PAGE_SIZE, WalChecksum.MetadataSize);
            metadata.Write(0L, 44);
            metadata.Write(0u, 4);
            metadata.Write(~Crc32C.Update(uint.MaxValue, bytes, frame, WalChecksum.FrameSize), 4);
            AssertRecovery(test.Data.ToArray(), bytes, null, false, 1, end);
        }

        private static void AssertRecovery(byte[] dataBytes, byte[] logBytes, string password, bool readOnly, int value, int end)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(logBytes);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == value);
                db.GetCollection("$database").FindAll().Single()["recoveryDiscardedWalBytes"].AsInt64.Should().BeGreaterThan(0);
                if (readOnly)
                {
                    data.ToArray().Should().Equal(dataBytes);
                    log.ToArray().Should().Equal(logBytes);
                    return;
                }
                log.Length.Should().Be(end);
                db.GetCollection("next").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                .And.OnlyContain(x => x["value"].AsInt32 == value);
            reopened.GetCollection("next").Count().Should().Be(1);
        }
    }
}
