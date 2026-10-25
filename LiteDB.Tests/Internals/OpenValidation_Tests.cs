using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class OpenValidation_Tests
    {
        [Theory]
        [InlineData(16, null, false)]
        [InlineData(20009, null, false)]
        [InlineData(16, "secret", false)]
        [InlineData(16384, "secret", false)]
        [InlineData(16, null, true)]
        [InlineData(20009, null, true)]
        [InlineData(16, "secret", true)]
        [InlineData(16384, "secret", true)]
        public void Foreign_caller_stream_is_unchanged_and_remains_owned_by_caller(int length, string password, bool readOnly)
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
            if (password != null)
            {
                bytes[0] = 1;
                if (length >= 64) Array.Clear(bytes, 32, 32);
            }
            using var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            Action open = () =>
            {
                using var engine = new LiteEngine(new EngineSettings
                {
                    DataStream = stream, Password = password, ReadOnly = readOnly
                });
            };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
            stream.CanRead.Should().BeTrue();
            stream.ToArray().Should().Equal(bytes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Encrypted_torn_tail_repair_respects_read_only(bool readOnly)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" }))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "original" });
            }
            var original = File.ReadAllBytes(file.Filename);
            var torn = original.Concat(new byte[5003]).ToArray();
            File.WriteAllBytes(file.Filename, torn);
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = "secret", ReadOnly = readOnly
            }))
            {
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("original");
                TempFile.ReadAllBytesShared(file.Filename).Should().Equal(readOnly ? torn : original);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(readOnly ? torn : original);
        }
    }
}
