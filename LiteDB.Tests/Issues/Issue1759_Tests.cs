using System;
using FluentAssertions;
using LiteDB;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #1759 - BufferExtensions.ToBytes(Int64/UInt64/Double) used an unaligned
    /// *(long*)ptr = value store. On 32-bit ARM (armeabi-v7a) this crashes with
    /// SIGBUS / BUS_ADRALN under IL2CPP or Mono-LLVM Release, because the compiler
    /// emits STRD/STM which require a word-aligned address. The write must be
    /// alignment-safe (byte-wise).
    ///
    /// The alignment fault itself cannot be observed on x86/x64 CI (those tolerate
    /// unaligned access), so these tests assert byte-level correctness at unaligned
    /// offsets to guard against a regression to the pointer-cast implementation.
    /// </summary>
    public class Issue1759_Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ToBytes_Int64_is_correct_at_unaligned_offset(int offset)
        {
            var value = long.MaxValue; // 0x7FFFFFFFFFFFFFFF - the value seen in real crashes
            var buffer = new byte[offset + 8];

            value.ToBytes(buffer, offset);

            BitConverter.ToInt64(buffer, offset).Should().Be(value);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ToBytes_UInt64_is_correct_at_unaligned_offset(int offset)
        {
            var value = ulong.MaxValue - 12345UL;
            var buffer = new byte[offset + 8];

            value.ToBytes(buffer, offset);

            BitConverter.ToUInt64(buffer, offset).Should().Be(value);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void ToBytes_Double_is_correct_at_unaligned_offset(int offset)
        {
            var value = Math.PI;
            var buffer = new byte[offset + 8];

            value.ToBytes(buffer, offset);

            BitConverter.ToDouble(buffer, offset).Should().Be(value);
        }
    }
}
