using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class ChecksumCheckpointValidation_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void StaleSafepointFrames_CannotBeCertifiedByCheckpoint(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            var originalData = source.Data.ToArray();
            source.Database.BeginTrans();
            source.Update("docs", 1);
            var transaction = source.Engine.GetMonitor().GetThreadTransaction();
            transaction.Safepoint();
            var stale = source.Log.ToArray();
            var positions = transaction.Pages.DirtyPages.Values.Select(x => x.Position).ToArray();
            positions.Should().NotBeEmpty();
            source.Update("docs", 2);
            transaction.Safepoint();
            source.Database.Commit();
            foreach (var position in positions)
            {
                var physical = checked((int)(position / PAGE_SIZE * WalChecksum.FrameSize)) + (password == null ? 0 : PAGE_SIZE);
                source.Log.Position = physical;
                source.Log.Write(stale, physical, WalChecksum.FrameSize);
            }
            // Genuine older frames still have correct CRCs, salt and positions.
            // Their transaction digest must be checked before any data is copied.
            Action checkpoint = () => source.Database.Checkpoint();
            checkpoint.Should().Throw<PageChecksumException>();
            source.Data.ToArray().SequenceEqual(originalData).Should().BeTrue();
            source.Recover("docs", checkpoint: true).Should().HaveCount(WalTestDatabase.DocumentCount)
                .And.OnlyContain(x => x["value"].AsInt32 == 0);
        }

        [Theory]
        [InlineData(null, "crc")]
        [InlineData("secret", "crc")]
        [InlineData(null, "count")]
        [InlineData("secret", "count")]
        [InlineData(null, "digest")]
        [InlineData("secret", "digest")]
        [InlineData(null, "sequence")]
        [InlineData("secret", "sequence")]
        [InlineData(null, "truncated")]
        [InlineData("secret", "truncated")]
        [InlineData(null, "confirmation")]
        [InlineData("secret", "confirmation")]
        public void CorruptCommittedWal_IsRejectedBeforeCheckpointChangesAnyData(string password, string damage)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            var originalData = source.Data.ToArray();
            source.Database.GetCollection("prefix").Insert(new BsonDocument { ["_id"] = 1 });
            source.Update("docs", 2);
            using (var factory = new StreamFactory(source.Log, password))
            using (var stream = factory.GetStream(true, false))
            {
                var frame = new byte[WalChecksum.FrameSize];
                var position = stream.Length - frame.Length;
                stream.Position = position;
                stream.ReadRequired(frame, 0, frame.Length);
                var metadata = new BufferSlice(frame, PAGE_SIZE, WalChecksum.MetadataSize);
                if (damage == "crc") frame[400] ^= 1;
                else
                {
                    if (damage == "count") metadata.Write(metadata.ReadUInt32(32) + 1, 32);
                    if (damage == "digest") metadata.Write(metadata.ReadInt64(36) ^ 1, 36);
                    if (damage == "sequence") metadata.Write(metadata.ReadInt64(44) + 1, 44);
                    if (damage == "confirmation")
                    {
                        frame[BasePage.P_IS_CONFIRMED] = 0;
                        metadata.Write(0L, 44);
                    }
                    metadata.Write(0u, 4);
                    metadata.Write(~Crc32C.Update(uint.MaxValue, frame, 0, frame.Length), 4);
                }
                if (damage == "truncated")
                {
                    stream.SetLength(position);
                    stream.Position = position;
                }
                else
                {
                    stream.Position = position;
                    stream.Write(frame, 0, frame.Length);
                }
                stream.FlushToDisk();
            }
            var damagedWal = source.Log.ToArray();
            Action checkpoint = () => source.Database.Checkpoint();
            checkpoint.Should().Throw<PageChecksumException>();
            source.Data.ToArray().SequenceEqual(originalData).Should().BeTrue("no data page may be copied from an unverified checkpoint batch");
            source.Log.ToArray().Should().Equal(damagedWal);
            Action write = () => source.Database.GetCollection("next").Insert(new BsonDocument { ["_id"] = 1 });
            write.Should().Throw<PageChecksumException>();

            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = ChecksumTestFiles.Copy(source.Log.ToArray());
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("prefix").Count().Should().Be(1);
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == 0);
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                .And.OnlyContain(x => x["value"].AsInt32 == 0);
        }
    }
}
