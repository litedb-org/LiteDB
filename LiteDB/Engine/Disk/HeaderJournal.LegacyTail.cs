using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class HeaderJournal
    {
        /// <summary>
        /// A released engine appended ordinary transactions after unsealed
        /// conversion records. The primary is still legacy and the WAL is an
        /// ordinary legacy WAL: the records are unconfirmed transaction 1.
        /// </summary>
        internal bool LegacyTail { get; private set; }

        /// <summary>
        /// Locates the first slot after the intact records of an unsealed
        /// header-only conversion. A torn record there is ignored; a page
        /// written by a released engine makes the whole WAL legacy redo.
        /// </summary>
        private static HeaderJournal ReadLegacyTail(Stream stream, byte[] intent)
        {
            var slot = (long)PAGE_SIZE;
            if (IsRedo(stream, slot, intent))
            {
                slot += PAGE_SIZE;
                if (ReadPrepared(stream, slot) != null) slot += PAGE_SIZE;
            }
            if (IsLegacyAppend(stream, slot, intent))
                return new HeaderJournal { Header = intent, LegacyTail = true, FooterBytes = 0 };
            // Conversion stops after one torn write. More bytes than that came
            // from another writer; refuse rather than discard them.
            if (stream.Length > slot + PAGE_SIZE) throw new PageChecksumException(FileOrigin.Log, slot);
            return null;
        }

        private static bool IsRedo(Stream stream, long position, byte[] intent)
        {
            if (stream.Length < position + PAGE_SIZE) return false;
            var bytes = new byte[PAGE_SIZE];
            stream.Position = position;
            stream.ReadRequired(bytes, 0, bytes.Length);
            for (var i = 0; i < PAGE_SIZE; i++)
                if ((i < P_MAGIC || i >= P_BODY_CRC + 4) && bytes[i] != intent[i]) return false;
            return true;
        }

        /// <summary>
        /// A torn conversion write keeps its record's first AES block (page 0,
        /// header links and transaction 1) or leaves a blank or garbage block
        /// that does not form a valid legacy page. Released engines number
        /// their transactions after the conversion's, and can only allocate
        /// one new page per WAL page they append.
        /// </summary>
        private static bool IsLegacyAppend(Stream stream, long position, byte[] record)
        {
            if (position < PAGE_SIZE || stream.Length < position + PAGE_SIZE) return false;
            var bytes = new byte[PAGE_SIZE];
            stream.Position = position;
            stream.ReadRequired(bytes, 0, bytes.Length);
            var torn = true;
            for (var i = 0; i < 16; i++) torn &= bytes[i] == record[i];
            if (torn) return false;
            var page = new BufferSlice(bytes, 0, PAGE_SIZE);
            var conversion = new BufferSlice(record, 0, PAGE_SIZE);
            var type = bytes[BasePage.P_PAGE_TYPE];
            var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
            var maxPageID = conversion.ReadUInt32(HeaderPage.P_LAST_PAGE_ID) + (stream.Length - position) / PAGE_SIZE;
            return bytes[BasePage.P_PAGE_FORMAT] == PageChecksum.Legacy && bytes[BasePage.P_IS_CONFIRMED] <= 1 &&
                page.ReadUInt32(BasePage.P_TRANSACTION_ID) > conversion.ReadUInt32(BasePage.P_TRANSACTION_ID) &&
                type <= (byte)PageType.VectorIndex && (type == (byte)PageType.Header) == (pageID == 0) && pageID <= maxPageID;
        }
    }
}
