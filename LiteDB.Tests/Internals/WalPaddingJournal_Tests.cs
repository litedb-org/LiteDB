using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class WalPaddingJournal_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FooterReachingDiskBeforeItsSync_CannotDependOnVolatilePadding(string password)
        {
            using var data = new MemoryStream();
            using var log = new FooterFirstDevice();
            var expected = Enumerable.Range(1, 16).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = 1, ["payload"] = new string('x', 1500)
            }).ToArray();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                db.Checkpoint();
                rows.Insert(expected);
                var content = log.Length - (password == null ? 0 : 8192);
                (content % 8256).Should().BeGreaterThan(0, "the probe requires alignment padding");
                log.Armed = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>();
                log.Fired.Should().BeTrue();
            }
            using var recoveredData = ChecksumTestFiles.Copy(data.ToArray());
            using var recoveredLog = ChecksumTestFiles.Copy(log.Durable);
            foreach (var readOnly in new[] { true, false, true })
            {
                var originalData = recoveredData.ToArray();
                var originalLog = recoveredLog.ToArray();
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
                    { DataStream = recoveredData, LogStream = recoveredLog, Password = password, ReadOnly = readOnly })))
                {
                    db.GetCollection("rows").FindAll().Should().BeEquivalentTo(expected);
                    db.GetCollection("rows").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(expected);
                    if (!readOnly) db.Checkpoint();
                }
                if (readOnly)
                {
                    recoveredData.ToArray().Should().Equal(originalData);
                    recoveredLog.ToArray().Should().Equal(originalLog);
                }
            }
        }

        private sealed class FooterFirstDevice : MemoryStream, IDurableStream
        {
            internal byte[] Durable = Array.Empty<byte>();
            internal bool Armed, Fired;

            public void FlushToDisk()
            {
                if (Fired) throw new IOException("power loss");
                Durable = ToArray();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Fired) throw new IOException("power loss");
                if (Armed && count == HeaderJournal.Size)
                {
                    // Persist the complete footer on top of the last synced
                    // image, losing any earlier writes still only in cache.
                    var position = checked((int)Position);
                    Array.Resize(ref Durable, position + count);
                    Buffer.BlockCopy(buffer, offset, Durable, position, count);
                    Fired = true;
                    throw new IOException("footer persisted before its flush");
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (Fired) throw new IOException("power loss");
            }
        }
    }
}
