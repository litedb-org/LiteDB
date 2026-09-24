using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;
#endif

namespace LiteDB.Engine
{
    /// <summary>Castagnoli CRC, with a portable slicing-by-eight fallback.</summary>
    internal static class Crc32C
    {
        private static readonly uint[] Table = CreateTable();

#if NET8_0_OR_GREATER
        // Page-sized loops are hot even during a short-lived engine open. Tier-0
        // span/helper calls otherwise make checksums expensive until the JIT tiers up.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        internal static uint Update(uint crc, byte[] bytes, int offset, int count)
        {
            var end = offset + count;
#if NET8_0_OR_GREATER
            if (Sse42.X64.IsSupported)
            {
                while (offset + 8 <= end)
                {
                    crc = (uint)Sse42.X64.Crc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)));
                    offset += 8;
                }
                while (offset < end) crc = Sse42.Crc32(crc, bytes[offset++]);
                return crc;
            }
            if (Crc32.Arm64.IsSupported)
            {
                while (offset + 8 <= end)
                {
                    crc = Crc32.Arm64.ComputeCrc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)));
                    offset += 8;
                }
                while (offset < end) crc = Crc32.ComputeCrc32C(crc, bytes[offset++]);
                return crc;
            }
#endif
            return UpdatePortable(crc, bytes, offset, count);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        internal static uint UpdatePortable(uint crc, byte[] bytes, int offset, int count)
        {
            var end = offset + count;
            while (offset + 8 <= end)
            {
                var first = crc ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                var second = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                crc = Table[1792 + (first & 255)] ^ Table[1536 + ((first >> 8) & 255)] ^
                    Table[1280 + ((first >> 16) & 255)] ^ Table[1024 + (first >> 24)] ^
                    Table[768 + (second & 255)] ^ Table[512 + ((second >> 8) & 255)] ^
                    Table[256 + ((second >> 16) & 255)] ^ Table[second >> 24];
                offset += 8;
            }
            while (offset < end) crc = Table[(crc ^ bytes[offset++]) & 255] ^ (crc >> 8);
            return crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u);
                table[i] = crc;
            }
            for (var i = 256; i < table.Length; i++)
            {
                var crc = table[i - 256];
                table[i] = table[crc & 255] ^ (crc >> 8);
            }
            return table;
        }
    }
}
