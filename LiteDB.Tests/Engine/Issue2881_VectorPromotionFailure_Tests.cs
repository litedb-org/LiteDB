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
        public void Failed_header_write_or_flush_prevents_vector_commit(bool failFlush)
        {
            using var data = new PromotionFailureStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings
            {
                DataStream = data,
                LogStream = log,
                CompactStorage = CompactStorageMode.Legacy
            };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "ordinary" });
                db.Checkpoint();
                data.FailFlush = failFlush;
                data.Armed = true;
                Action insert = () => docs.Insert(new BsonDocument
                {
                    ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f })
                });
                insert.Should().Throw<IOException>().WithMessage("Injected promotion failure");
                data.Triggered.Should().BeTrue();
                log.Length.Should().Be(2 * Constants.PAGE_SIZE,
                    "only the durable header recovery record may precede format promotion");
                var journal = log.ToArray();
                new PageBuffer(journal, 0, 0).IsBlank().Should().BeTrue();
                new PageBuffer(journal, Constants.PAGE_SIZE, 0).IsBlank().Should().BeTrue();
            }
            using var reopened = new LiteDatabase(new LiteEngine(settings));
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
                if (Armed && Position == 0 && count == Constants.PAGE_SIZE && buffer[offset + HeaderPage.P_FILE_VERSION] == 9)
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
