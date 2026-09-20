using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Issue2881_VectorPromotionFailure_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_header_write_or_flush_prevents_index_migration(bool failFlush)
        {
            using var data = new PromotionFailureStream();
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(data, logStream: log))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "ordinary" });
                db.Checkpoint();
            }
            data.Position = HeaderPage.P_FILE_VERSION;
            data.WriteByte(8);
            data.Position = EnginePragmas.P_INDEX_ORDER_VERSION;
            data.WriteByte(0);
            data.FailFlush = failFlush;
            data.Armed = true;
            Action open = () => { using var db = new LiteDatabase(data, logStream: log); };
            open.Should().Throw<IOException>().WithMessage("Injected promotion failure");
            data.Triggered.Should().BeTrue();
            log.Length.Should().Be(0, "no migrated page may enter the WAL before the format is durable");
            using var reopened = new LiteDatabase(data, logStream: log);
            reopened.GetCollection("docs").Count().Should().Be(1);
            reopened.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("ordinary");
        }

        private sealed class PromotionFailureStream : MemoryStream
        {
            internal bool Armed;
            internal bool FailFlush;
            internal bool Triggered;
            private bool _promotionWritten;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == 0 && count == Constants.PAGE_SIZE && buffer[offset + HeaderPage.P_FILE_VERSION] == 10)
                {
                    if (!FailFlush) this.Fail();
                    _promotionWritten = true;
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (Armed && FailFlush && _promotionWritten) this.Fail();
                base.Flush();
            }

            private void Fail()
            {
                Armed = false;
                Triggered = true;
                throw new IOException("Injected promotion failure");
            }
        }
    }
}
