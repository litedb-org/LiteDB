using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2767_Recovery_Tests
    {
        [Fact]
        public void Partial_blank_encrypted_read_preserves_buffer_bytes_beyond_the_result()
        {
            // ArrayPool buffers retain other callers' data; the zero-block sentinel
            // must not depend on the contents of a reused encryption scratch buffer.
            var dirty = ArrayPool<byte>.Shared.Rent(16);
            for (var i = 0; i < dirty.Length; i++) dirty[i] = 0x7f;
            ArrayPool<byte>.Shared.Return(dirty);
            using var backing = new MemoryStream();
            using var crypto = new AesStream("short-read-password", backing);
            backing.SetLength(16384); // Hidden header plus one unwritten encrypted page.
            crypto.Position = 0;
            var buffer = Enumerable.Repeat((byte)0xcc, 16384).ToArray();

            crypto.Read(buffer, 0, buffer.Length).Should().Be(8192);
            buffer.Take(8192).Should().OnlyContain(x => x == 0);
            buffer.Skip(8192).Should().OnlyContain(x => x == 0xcc);
        }

        private sealed class ShortReadStream : MemoryStream
        {
            public ShortReadStream(byte[] bytes) : base(bytes) { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, Math.Min(count, 1));
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Current_recovery_reader_and_format_detection_accept_short_reads(bool encrypted, bool empty)
        {
            using var source = new MemoryStream();
            var password = encrypted ? "recovery-password" : null;
            var payload = Enumerable.Range(0, 20000).Select(i => (byte)(i * 23)).ToArray();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = source, Password = password
            })))
            {
                if (!empty)
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = payload });
                }
                db.Checkpoint();
            }
            var original = source.ToArray();
            using var shortReads = new ShortReadStream(original.ToArray());
            var settings = new EngineSettings { DataStream = shortReads, Password = password };
            // Rebuild installation requires files; exercise its stream-backed detection and reader directly.
            Action classify = () => new RebuildService(settings);
            classify.Should().NotThrow();
            var errors = new List<FileReaderError>();
            using (var reader = new FileReaderV8(settings, errors))
            {
                reader.Open();
                if (empty)
                {
                    reader.GetCollections().Should().BeEmpty();
                }
                else
                {
                    reader.GetCollections().Should().Equal("rows");
                    var row = reader.GetDocuments("rows").Single();
                    row["_id"].AsInt32.Should().Be(1);
                    row["payload"].AsBinary.Should().Equal(payload);
                }
                errors.Should().BeEmpty();
            }
            shortReads.ToArray().Should().Equal(original);
        }

        [Theory]
        [InlineData("v4.db", null)]
        [InlineData("Issue_2494_EncryptedV4.db", "pass123")]
        public void Legacy_upgrade_reader_fills_pages_before_parsing_or_decrypting(string fixture, string password)
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "Resources", fixture));
            var original = File.ReadAllBytes(path);
            using var shortReads = new ShortReadStream(original.ToArray());
            var settings = new EngineSettings { DataStream = shortReads, Password = password };
            Action classify = () => new RebuildService(settings);
            classify.Should().NotThrow();
            using (var reader = new FileReaderV7(settings))
            {
                reader.Open();
                if (password == null)
                {
                    reader.GetDocuments("col1").Should().HaveCount(3);
                }
                else
                {
                    reader.GetDocuments("PlayerDto")
                        .Select(x => x["_id"].AsGuid.ToString() + ":" + x["Name"].AsString)
                        .OrderBy(x => x, StringComparer.Ordinal).Should().Equal(
                            "4ac8f759-248f-4114-8be6-e510ad4e140d:Jesse",
                            "db503008-84d5-42d8-b372-d7616ea133f1:Bob");
                }
            }
            shortReads.ToArray().Should().Equal(original);
        }
    }
}
