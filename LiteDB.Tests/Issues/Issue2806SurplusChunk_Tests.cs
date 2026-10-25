using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2806SurplusChunk_Tests
    {
        private static LiteDatabase WithOversizedLastChunk(out long declaredLength)
        {
            var db = new LiteDatabase(":memory:");
            var info = db.FileStorage.Upload("f", "f.bin", new MemoryStream(Enumerable.Repeat((byte)7, 300_000).ToArray()));
            declaredLength = info.Length;

            var chunks = db.GetCollection("_chunks");
            var last = chunks.FindAll().OrderBy(x => x["_id"]["n"].AsInt32).Last();
            last["data"] = last["data"].AsBinary.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
            chunks.Update(last).Should().BeTrue();

            return db;
        }

        [Fact]
        public void Download_rejects_a_chunk_that_extends_beyond_the_declared_length()
        {
            using var db = WithOversizedLastChunk(out _);

            Action download = () => db.FileStorage.Download("f", new MemoryStream());

            download.Should().Throw<LiteException>();
        }

        [Fact]
        public void Reading_exactly_the_declared_length_never_returns_surplus_bytes()
        {
            using var db = WithOversizedLastChunk(out var declaredLength);
            using var reader = db.FileStorage.OpenRead("f");
            var buffer = new byte[declaredLength];
            var total = 0;
            int read;

            while (total < buffer.Length && (read = reader.Read(buffer, total, buffer.Length - total)) > 0) total += read;

            total.Should().Be((int)declaredLength);

            var tail = Record.Exception(() => reader.Read(new byte[16], 0, 16).Should().Be(0));
            if (tail != null) tail.Should().BeOfType<LiteException>();
        }
    }
}
