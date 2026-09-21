using System;
using System.Linq;

using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2966_Tests
    {
        [Fact]
        public void Truncated_scalar_reports_LiteException_instead_of_index_error()
        {
            var bytes = new byte[] { 8, 0, 0, 0, 0x08, 0 };
            using var reader = new BufferReader(bytes);
            Action read = () => reader.ReadDocument().GetValue();
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Missing_final_terminator_is_rejected()
        {
            var valid = BsonSerializer.Serialize(new BsonDocument { ["value"] = true });
            var truncated = valid.Take(valid.Length - 1).ToArray();
            Action read = () => BsonSerializer.Deserialize(truncated);
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Oversized_binary_length_is_rejected_before_allocation()
        {
            var bytes = new byte[]
            {
                0x1c, 0, 0, 0, 0x05, 0x08, 0x10, 0, 0, 0, 0, 0x04, 0xb6, 0x68,
                0x5c, 0x4b, 0x1a, 0x27, 0xeb, 0x1c, 0xb2, 0xe9, 0x76, 0xb8, 0x20, 0x2b, 0x73, 0x5e
            };
            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Element_cannot_overrun_its_declared_container_boundary()
        {
            var bytes = new byte[] { 7, 0, 0, 0, 0x08, 0, 1, 0 };
            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>();
        }
    }
}
