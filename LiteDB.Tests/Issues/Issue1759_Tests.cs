using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #1759 - ToBytes(Int64/UInt64/Double) stored 8 bytes through a single
    /// `*(long*)ptr = value` write. On 32-bit ARM (armeabi-v7a) that is lowered to
    /// STRD/STM, which fault with SIGBUS/BUS_ADRALN when the destination is not
    /// word-aligned, so writing e.g. the LIMIT_SIZE pragma (page offset 101) killed
    /// the process. The store is now done byte by byte, which has no alignment
    /// requirement.
    ///
    /// The alignment fault itself cannot be observed on x86/x64 (unaligned access is
    /// allowed there), so these tests pin down the two properties that must hold on
    /// every platform: the bytes stay in the platform's native order, and the value
    /// survives a write/read round trip through the buffer at unaligned offsets.
    /// </summary>
    public class Issue1759_Tests
    {
        public static TheoryData<int> UnalignedOffsets => new TheoryData<int> { 0, 1, 2, 3, 5, 7 };

        [Theory]
        [MemberData(nameof(UnalignedOffsets))]
        public void ToBytes_Int64_keeps_native_byte_order(int offset)
        {
            foreach (var value in new[] { long.MaxValue, long.MinValue, -1L, 1L, 0x0102030405060708L })
            {
                var buffer = new byte[offset + 8];

                value.ToBytes(buffer, offset);

                // BitConverter is native-endian, and so are the matching readers, so the
                // bytes written must be byte-for-byte what BitConverter produces.
                buffer.AsSpan(offset, 8).ToArray()
                    .Should().Equal(BitConverter.GetBytes(value), "value {0} at offset {1}", value, offset);
            }
        }

        [Theory]
        [MemberData(nameof(UnalignedOffsets))]
        public void ToBytes_UInt64_keeps_native_byte_order(int offset)
        {
            foreach (var value in new[] { ulong.MaxValue, ulong.MinValue, 1UL, 0x0102030405060708UL })
            {
                var buffer = new byte[offset + 8];

                value.ToBytes(buffer, offset);

                buffer.AsSpan(offset, 8).ToArray()
                    .Should().Equal(BitConverter.GetBytes(value), "value {0} at offset {1}", value, offset);
            }
        }

        [Theory]
        [MemberData(nameof(UnalignedOffsets))]
        public void ToBytes_Double_keeps_native_byte_order(int offset)
        {
            foreach (var value in new[] { Math.PI, 0d, -1.5d, double.MaxValue, double.MinValue })
            {
                var buffer = new byte[offset + 8];

                value.ToBytes(buffer, offset);

                buffer.AsSpan(offset, 8).ToArray()
                    .Should().Equal(BitConverter.GetBytes(value), "value {0} at offset {1}", value, offset);
            }
        }

        [Theory]
        [MemberData(nameof(UnalignedOffsets))]
        public void BufferSlice_write_read_round_trips_at_unaligned_offset(int offset)
        {
            // Exercises the real writer + reader pair, so it fails on any platform where
            // the two disagree about byte order - not just on little-endian hosts.
            var slice = new BufferSlice(new byte[offset + 8 + 8], offset, 16);

            slice.Write(long.MaxValue, 1);
            slice.ReadInt64(1).Should().Be(long.MaxValue);

            slice.Write(Math.PI, 1);
            slice.ReadDouble(1).Should().Be(Math.PI);

            var utcNow = DateTime.UtcNow;
            slice.Write(utcNow, 1);
            slice.ReadDateTime(1).Should().BeCloseTo(utcNow, TimeSpan.FromMilliseconds(1));
        }
    }
}
