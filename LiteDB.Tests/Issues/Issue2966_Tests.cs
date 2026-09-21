using System;
using System.Collections.Generic;
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
        public void Invalid_guid_length_is_rejected_before_reading_its_payload()
        {
            var bytes = new byte[]
            {
                30, 0, 0, 0,
                0x05, (byte)'g', 0,
                17, 0, 0, 0, 0x04
            };

            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>()
                .WithMessage("*GUID binary value must contain 16 bytes*");
        }

        [Fact]
        public void Binary_payload_is_bounded_by_its_container_before_allocation()
        {
            var bytes = new byte[]
            {
                13, 0, 0, 0,
                0x05, (byte)'b', 0,
                0x40, 0x42, 0x0f, 0, 0x00,
                0
            };

            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>()
                .WithMessage("*binary payload exceeds its container boundary*");
        }

        [Fact]
        public void Zero_length_header_sentinel_is_rejected_for_nested_documents()
        {
            var bytes = new byte[]
            {
                12, 0, 0, 0,
                0x03, (byte)'n', 0,
                0, 0, 0, 0,
                0
            };
            using var reader = new BufferReader(bytes) { AllowZeroLengthDocument = true };

            Action read = () => reader.ReadDocument().GetValue();
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Element_cannot_overrun_its_declared_container_boundary()
        {
            var bytes = new byte[] { 7, 0, 0, 0, 0x08, 0, 1, 0 };
            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Projected_out_string_still_requires_its_terminator()
        {
            var bytes = MalformedStringDocument();
            Action full = () => BsonSerializer.Deserialize(bytes);
            Action projected = () => ReadProjected(bytes, "keep");

            full.Should().Throw<LiteException>();
            projected.Should().Throw<LiteException>();
        }

        [Fact]
        public void Projected_out_nested_document_still_validates_its_boundary()
        {
            var nested = new byte[] { 7, 0, 0, 0, 0x08, 0, 1, 0 };
            var bytes = new byte[]
            {
                28, 0, 0, 0,
                0x03, (byte)'b', (byte)'a', (byte)'d', 0,
                nested[0], nested[1], nested[2], nested[3], nested[4], nested[5], nested[6], nested[7],
                0x10, (byte)'k', (byte)'e', (byte)'e', (byte)'p', 0, 1, 0, 0, 0,
                0
            };

            Action projected = () => ReadProjected(bytes, "keep");
            projected.Should().Throw<LiteException>();
        }

        [Fact]
        public void Projection_validates_fields_after_the_selected_value()
        {
            var bytes = new byte[]
            {
                26, 0, 0, 0,
                0x10, (byte)'k', (byte)'e', (byte)'e', (byte)'p', 0, 1, 0, 0, 0,
                0x02, (byte)'b', (byte)'a', (byte)'d', 0, 2, 0, 0, 0, (byte)'x', 1,
                0
            };

            Action projected = () => ReadProjected(bytes, "keep");
            projected.Should().Throw<LiteException>();
        }

        [Fact]
        public void Projected_out_binary_still_validates_its_length()
        {
            var bytes = new byte[]
            {
                15, 0, 0, 0,
                0x05, (byte)'b', (byte)'a', (byte)'d', 0,
                0xff, 0xff, 0xff, 0x7f, 0,
                0
            };

            Action projected = () => ReadProjected(bytes, "keep");
            projected.Should().Throw<LiteException>();
        }

        [Fact]
        public void Segmented_projection_uses_the_same_string_validation()
        {
            var bytes = MalformedStringDocument();
            var segments = bytes.Select((_, index) => new BufferSlice(bytes, index, 1)).ToArray();
            using var reader = new BufferReader(segments);
            Action projected = () => reader.ReadDocument(new HashSet<string> { "keep" }).GetValue();
            projected.Should().Throw<LiteException>();
        }

        [Theory]
        [InlineData(0x03)]
        [InlineData(0x04)]
        public void Deeply_nested_projected_out_containers_are_rejected_without_stack_overflow(int containerType)
        {
            var nested = BuildNestedContainer(BufferReader.MAX_BSON_NESTING_DEPTH, (byte)containerType);
            var bytes = new List<byte> { 0, 0, 0, 0 };
            bytes.AddRange(new byte[]
            {
                0x10, (byte)'k', (byte)'e', (byte)'e', (byte)'p', 0, 1, 0, 0, 0,
                (byte)containerType, (byte)'d', (byte)'e', (byte)'e', (byte)'p', 0
            });
            bytes.AddRange(nested);
            bytes.Add(0);
            WriteInt32(bytes, 0, bytes.Count);

            Action full = () => BsonSerializer.Deserialize(bytes.ToArray());
            Action projected = () => ReadProjected(bytes.ToArray(), "keep");

            full.Should().Throw<LiteException>().WithMessage("*nesting depth*");
            projected.Should().Throw<LiteException>().WithMessage("*nesting depth*");
        }

        private static BsonDocument ReadProjected(byte[] bytes, string field)
        {
            using var reader = new BufferReader(bytes);
            return reader.ReadDocument(new HashSet<string> { field }).GetValue();
        }

        private static byte[] MalformedStringDocument() => new byte[]
        {
            26, 0, 0, 0,
            0x02, (byte)'b', (byte)'a', (byte)'d', 0, 2, 0, 0, 0, (byte)'x', 1,
            0x10, (byte)'k', (byte)'e', (byte)'e', (byte)'p', 0, 1, 0, 0, 0,
            0
        };

        private static byte[] BuildNestedContainer(int wrappers, byte containerType)
        {
            var nested = new List<byte> { 5, 0, 0, 0, 0 };

            for (var i = 0; i < wrappers; i++)
            {
                var parent = new List<byte> { 0, 0, 0, 0, containerType, (byte)'x', 0 };
                parent.AddRange(nested);
                parent.Add(0);
                WriteInt32(parent, 0, parent.Count);
                nested = parent;
            }

            return nested.ToArray();
        }

        private static void WriteInt32(IList<byte> bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}
