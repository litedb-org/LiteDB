using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2052_Tests
    {
        [Theory]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void Malformed_BSON_date_is_rejected_without_modifying_input_or_hiding_valid_dates(long milliseconds)
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Independently authored BSON: length + datetime tag + cstring + Int64 + terminator.
                writer.Write(19);
                writer.Write((byte)0x09);
                writer.Write(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'e', 0 });
                writer.Write(milliseconds);
                writer.Write((byte)0);
                bytes = stream.ToArray();
            }
            var before = (byte[])bytes.Clone();
            var failure = Record.Exception(() => BsonSerializer.Deserialize(bytes));
            failure.Should().BeOfType<LiteException>("invalid stored BSON needs a database-format error rather than an unstructured DateTime overflow");
            bytes.Should().Equal(before);
            var valid = new BsonDocument { ["date"] = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), ["value"] = "control" };
            var recovered = BsonSerializer.Deserialize(BsonSerializer.Serialize(valid));
            recovered["date"].AsDateTime.ToUniversalTime().Should().Be(valid["date"].AsDateTime);
            recovered["value"].AsString.Should().Be("control");
        }
    }
}
