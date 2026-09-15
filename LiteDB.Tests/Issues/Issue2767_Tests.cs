using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2767_Tests
    {
        private sealed class ShortReadStream : MemoryStream
        {
            private readonly int _chunk;
            public int ShortReads { get; private set; }
            public ShortReadStream(byte[] bytes, int chunk) : base(bytes) { _chunk = chunk; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var actual = base.Read(buffer, offset, Math.Min(count, _chunk));
                if (actual > 0 && actual < count) ShortReads++;
                return actual;
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(257)]
        [InlineData(4095)]
        public void Valid_short_read_stream_opens_and_returns_all_payload_bytes(int chunk)
        {
            using var image = new MemoryStream();
            var payload = Enumerable.Range(0, 20000).Select(i => (byte)(i * 17)).ToArray();
            using (var db = new LiteDatabase(image))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["bytes"] = payload });
                db.Checkpoint();
            }
            var original = image.ToArray();
            using var shortReads = new ShortReadStream(original.ToArray(), chunk);
            using (var db = new LiteDatabase(shortReads))
            {
                db.GetCollection("rows").FindById(1)["bytes"].AsBinary.Should().Equal(payload);
                db.GetCollection("rows").Count().Should().Be(1);
                shortReads.ShortReads.Should().BeGreaterThan(0, "the stream must actually exercise partial reads");
            }
            shortReads.ToArray().Should().Equal(original);
        }
    }
}
