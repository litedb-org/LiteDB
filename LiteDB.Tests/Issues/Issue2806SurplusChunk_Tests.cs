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
        public void Reading_exactly_the_declared_length_rejects_the_surplus_chunk()
        {
            using var db = WithOversizedLastChunk(out var declaredLength);
            using var reader = db.FileStorage.OpenRead("f");
            var buffer = new byte[declaredLength];

            Action read = () =>
            {
                var total = 0;
                int bytes;

                while (total < buffer.Length && (bytes = reader.Read(buffer, total, buffer.Length - total)) > 0) total += bytes;
            };

            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Reading_rejects_a_file_whose_chunks_overrun_the_declared_length()
        {
            using var db = new LiteDatabase(":memory:");
            db.FileStorage.Upload("f", "f.bin", new MemoryStream(Enumerable.Repeat((byte)7, 600_000).ToArray()));

            var chunks = db.GetCollection("_chunks");
            var middle = chunks.FindAll().OrderBy(x => x["_id"]["n"].AsInt32).Skip(1).First();
            middle["data"] = middle["data"].AsBinary.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
            chunks.Update(middle).Should().BeTrue();

            Action download = () => db.FileStorage.Download("f", new MemoryStream());

            download.Should().Throw<LiteException>();
        }

        [Fact]
        public void Seeking_into_a_chunk_that_extends_beyond_the_declared_length_is_rejected()
        {
            using var db = WithOversizedLastChunk(out var declaredLength);
            using var reader = db.FileStorage.OpenRead("f");

            Action seekAndRead = () =>
            {
                reader.Seek(declaredLength - 10, SeekOrigin.Begin);
                reader.Read(new byte[10], 0, 10).Should().Be(10);
            };

            seekAndRead.Should().Throw<LiteException>();
        }
    }
}
