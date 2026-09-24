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
            var confirmation = false;
            if (IsRedo(stream, slot, intent))
            {
                slot += PAGE_SIZE;
                confirmation = ReadPrepared(stream, slot) != null;
                if (confirmation) slot += PAGE_SIZE;
            }
            if (IsLegacyAppend(stream, slot, intent, confirmation))
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
        /// header links and the low half of transaction 1) or leaves a blank or
        /// garbage block that does not form a valid legacy page. Released
        /// engines number their transactions after the conversion's, and can
        /// only allocate one new page per WAL page they append.
        /// <paramref name="confirmation"/> marks the slot of the conversion's
        /// own confirmation page.
        /// </summary>
        private static bool IsLegacyAppend(Stream stream, long position, byte[] record, bool confirmation)
        {
            if (position < PAGE_SIZE || stream.Length < position + PAGE_SIZE) return false;
            var bytes = new byte[PAGE_SIZE];
            stream.Position = position;
            stream.ReadRequired(bytes, 0, bytes.Length);
            var sharesFirstBlock = true;
            for (var i = 0; i < 16; i++) sharesFirstBlock &= bytes[i] == record[i];
            if (sharesFirstBlock && !IsConfirmedHeaderCommit(bytes, record, position, confirmation)) return false;
            var page = new BufferSlice(bytes, 0, PAGE_SIZE);
            var conversion = new BufferSlice(record, 0, PAGE_SIZE);
            var type = bytes[BasePage.P_PAGE_TYPE];
            var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
            var maxPageID = conversion.ReadUInt32(HeaderPage.P_LAST_PAGE_ID) + (stream.Length - position) / PAGE_SIZE;
            return bytes[BasePage.P_PAGE_FORMAT] == PageChecksum.Legacy && bytes[BasePage.P_IS_CONFIRMED] <= 1 &&
                page.ReadUInt32(BasePage.P_TRANSACTION_ID) > conversion.ReadUInt32(BasePage.P_TRANSACTION_ID) &&
                type <= (byte)PageType.VectorIndex && (type == (byte)PageType.Header) == (pageID == 0) && pageID <= maxPageID;
        }

        /// <summary>
        /// A header-only commit whose transaction ID is congruent to 1 modulo
        /// 2^16 shares the record's first block. Its second block still differs:
        /// it is confirmed, carries the high half of its transaction ID, and
        /// the rest is the unchanged header layout. Plain tears leave record or
        /// zero bytes there and encrypted tears a whole garbage block, so a
        /// torn write cannot produce it. Only the conversion's own confirmation
        /// page matches it with transaction 1. Released engines continue after
        /// that ID, so a confirmed transaction 1 elsewhere is refused.
        /// </summary>
        private static bool IsConfirmedHeaderCommit(byte[] bytes, byte[] record, long position, bool confirmation)
        {
            if (record[BasePage.P_IS_CONFIRMED] != 0 || bytes[BasePage.P_IS_CONFIRMED] != 1) return false;
            for (var i = BasePage.P_IS_CONFIRMED + 1; i < 32; i++)
                if (bytes[i] != record[i]) return false;
            var transactionID = new BufferSlice(bytes, 0, PAGE_SIZE).ReadUInt32(BasePage.P_TRANSACTION_ID);
            if (transactionID != new BufferSlice(record, 0, PAGE_SIZE).ReadUInt32(BasePage.P_TRANSACTION_ID)) return true;
            if (confirmation) return false;
            throw new PageChecksumException(FileOrigin.Log, position);
        }
    }
}
