using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class PageChecksum
    {
        // Transaction IDs have no meaning in checkpointed data pages. Format v10
        // uses those four bytes for a checksum, without moving any page payload.
        private static readonly byte[] Zero = new byte[4];

        internal static uint Compute(BufferSlice page)
        {
            var crc = Crc32C.Update(uint.MaxValue, page.Array, page.Offset, BasePage.P_TRANSACTION_ID);
            crc = Crc32C.Update(crc, Zero, 0, Zero.Length);
            return ~Crc32C.Update(crc, page.Array, page.Offset + BasePage.P_IS_CONFIRMED,
                PAGE_SIZE - BasePage.P_IS_CONFIRMED);
        }

        internal static void Write(BufferSlice page) => page.Write(Compute(page), BasePage.P_TRANSACTION_ID);

        internal static void Validate(BufferSlice page, long position)
        {
            if (page.ReadUInt32(BasePage.P_TRANSACTION_ID) != Compute(page) ||
                page.ReadUInt32(BasePage.P_PAGE_ID) != position / PAGE_SIZE)
                throw new PageChecksumException(FileOrigin.Data, position);
        }
    }

    internal sealed class PageChecksumException : LiteException
    {
        internal PageChecksumException(FileOrigin origin, long position)
            : base(LiteException.CHECKSUM_MISMATCH, "Checksum mismatch in {0} file at position {1}.", origin, position)
        {
        }
    }
}
