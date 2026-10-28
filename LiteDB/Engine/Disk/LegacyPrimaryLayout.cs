using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class LegacyPrimaryLayout
    {
        internal static bool IsIdentified(Stream primary, BufferSlice header)
        {
            // Missing modern markers in a damaged header prove nothing. Require
            // the complete legacy header prefix AND independent collection,
            // index and data pages. Any modern page defeats this classification.
            // Version is deliberately not evidence: the unsupported-version
            // controls have real v5 bodies with only that byte changed.
            if (header.ReadString(HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length) != HeaderPage.HEADER_INFO ||
                header.ReadUInt32(BasePage.P_PAGE_ID) != 0 || header[BasePage.P_PAGE_TYPE] != (byte)PageType.Header ||
                header[BasePage.P_PAGE_FORMAT] != PageChecksum.Legacy ||
                header.ReadUInt32(BasePage.P_TRANSACTION_ID) != uint.MaxValue)
                return false;
            for (var i = WalChecksum.SaltPosition; i < HeaderPage.P_INVALID_DATAFILE_STATE; i++)
                if (header[i] != 0) return false;
            var length = primary.Length;
            if (length % PAGE_SIZE != 0 || length < 4L * PAGE_SIZE ||
                header.ReadUInt32(HeaderPage.P_LAST_PAGE_ID) != length / PAGE_SIZE - 1)
                return false;

            var position = primary.Position;
            try
            {
                var bytes = new byte[PAGE_HEADER_SIZE];
                var page = new BufferSlice(bytes, 0, bytes.Length);
                var kinds = 0;
                for (long offset = PAGE_SIZE; offset < length; offset += PAGE_SIZE)
                {
                    primary.Position = offset;
                    primary.ReadRequired(bytes, 0, bytes.Length);
                    var type = page[BasePage.P_PAGE_TYPE];
                    if (page[BasePage.P_PAGE_FORMAT] != PageChecksum.Legacy ||
                        page.ReadUInt32(BasePage.P_PAGE_ID) != offset / PAGE_SIZE ||
                        type < (byte)PageType.Collection || type > (byte)PageType.Data ||
                        page.ReadUInt32(BasePage.P_TRANSACTION_ID) != uint.MaxValue ||
                        page[BasePage.P_IS_CONFIRMED] != 0 ||
                        page.ReadUInt32(BasePage.P_COL_ID) == 0 ||
                        page.ReadUInt32(BasePage.P_COL_ID) >= length / PAGE_SIZE ||
                        page.ReadUInt16(BasePage.P_NEXT_FREE_POSITION) < PAGE_HEADER_SIZE ||
                        page.ReadUInt16(BasePage.P_NEXT_FREE_POSITION) > PAGE_SIZE ||
                        page.ReadUInt16(BasePage.P_USED_BYTES) + page.ReadUInt16(BasePage.P_FRAGMENTED_BYTES) > PAGE_SIZE - PAGE_HEADER_SIZE)
                        return false;
                    kinds |= 1 << type;
                }
                return kinds == ((1 << (int)PageType.Collection) | (1 << (int)PageType.Index) | (1 << (int)PageType.Data));
            }
            finally { primary.Position = position; }
        }
    }
}
