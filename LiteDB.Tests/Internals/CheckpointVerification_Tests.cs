using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class CheckpointVerification_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void File_checkpoint_preserves_rows_indexes_and_nonzero_padding(bool nonzeroPadding)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                rows.Insert(Enumerable.Range(0, 64).Select(id =>
                    new BsonDocument { ["_id"] = id, ["value"] = id % 4 }));
                var disk = Disk(engine);
                lock (disk.WalWriterLock)
                {
                    var stream = ((ChecksummedWalStream)disk.WalWriterLock).RawStream;
                    var frameEnd = stream.Length / WalChecksum.FrameSize * WalChecksum.FrameSize;
                    frameEnd.Should().BeLessThan(stream.Length);
                    if (nonzeroPadding)
                    {
                        stream.Position = frameEnd;
                        stream.WriteByte(99);
                        stream.Flush();
                    }
                    var transactions = new HashSet<uint>(disk.ReadFull(FileOrigin.Log)
                        .Where(page => page.ReadBool(BasePage.P_IS_CONFIRMED))
                        .Select(page => page.ReadUInt32(BasePage.P_TRANSACTION_ID)));
                    disk.TryVerifyCheckpointBody(transactions, out _).Should().Be(!nonzeroPadding);
                }
                db.Checkpoint();
            }
            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32)
                    .Should().BeEquivalentTo(Enumerable.Range(0, 64));
                db.GetCollection("rows").Find(Query.EQ("value", 2)).Select(row => row["_id"].AsInt32)
                    .Should().BeEquivalentTo(Enumerable.Range(0, 64).Where(id => id % 4 == 2));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Corrupt_file_checkpoint_fails_before_modifying_either_source(string password)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 11 });
            var disk = Disk(engine);
            lock (disk.WalWriterLock)
            {
                var raw = ((ChecksummedWalStream)disk.WalWriterLock).RawStream;
                var block = new byte[16];
                raw.Position = 400;
                raw.ReadRequired(block, 0, block.Length);
                block[0] ^= 1;
                raw.Position = 400;
                raw.Write(block, 0, block.Length);
                raw.Flush();
            }
            var data = TempFile.ReadAllBytesShared(file.Filename);
            var logPath = FileHelper.GetLogFile(file.Filename);
            var log = TempFile.ReadAllBytesShared(logPath);
            Action checkpoint = () => db.Checkpoint();
            checkpoint.Should().Throw<PageChecksumException>();
            TempFile.ReadAllBytesShared(file.Filename).Should().Equal(data);
            TempFile.ReadAllBytesShared(logPath).Should().Equal(log);
        }

        private static DiskService Disk(LiteEngine engine) => (DiskService)typeof(LiteEngine)
            .GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
    }
}
