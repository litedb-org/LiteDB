using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // The header ValidateExistingData read, recovered and validated; nothing can write
        // the header before LiteEngine.Open reads it (same engine, still opening).
        private byte[] _openingHeader;

        /// <summary>
        /// The opening header already read and validated by this disk service, once;
        /// null afterwards (and for new files), when callers read it with ReadFull.
        /// </summary>
        internal PageBuffer TakeOpeningHeader()
        {
            var bytes = _openingHeader;
            _openingHeader = null;
            return bytes == null ? null : new PageBuffer(bytes, 0, 0) { Position = 0, Origin = FileOrigin.Data, ShareCounter = 0 };
        }

        private HeaderPage ValidateExistingData()
        {
            var stream = _dataPool.Rent();
            try
            {
                var bytes = new byte[PAGE_SIZE];
                stream.Position = 0;
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw LiteException.InvalidDatabase();
                    offset += read;
                }

                this.RecoverHeaderJournal(ref bytes, stream);
                if (bytes[0] == 1)
                    throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                // Validate identity and the complete header before permitting any repair.
                this.LoadChecksums(new BufferSlice(bytes, 0, PAGE_SIZE));
                _openingHeader = (byte[])bytes.Clone();
                var header = new HeaderPage(new PageBuffer(bytes, 0, 0));
                // Only a valid legacy header can meet the WAL of its own conversion.
                if (!ChecksumsEnabled) this.RejectConvertedWal();
                return header;
            }
            finally
            {
                _dataPool.Return(stream);
            }
        }

        /// <summary>
        /// A legacy header next to a WAL of checksummed frames: a mismatched restore/copy, or
        /// storage losing a synced conversion header while retaining newer WAL bytes. This is not
        /// an ordinary crash on storage honoring sync. Legacy rules would replay frames as pages at
        /// positions read from their trailers, so refuse the open; it changes neither file. A converted
        /// WAL starts over at its first frame, whose trailer lands on a legacy page's page ID, which
        /// no document controls. Journal recovery also preflights every complete legacy slot before
        /// its validated journal records, plus any tail after a confirmed footer.
        /// </summary>
        private void RejectConvertedWal(long? legacyLength = null)
        {
            // GetLength is plaintext length: an incomplete encrypted preamble has no content
            // and must stay untouched. A page-format marker needs only the first 32 bytes,
            // even when the payload or its checksum trailer was torn.
            if (!_logFactory.Exists()) return;
            var length = legacyLength ?? _logFactory.GetLength();
            if (length <= BasePage.P_PAGE_FORMAT) return;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                var log = reader.RawStream;
                var prefix = new byte[BasePage.P_PAGE_FORMAT + 1];
                // Ordinary replay checks later complete pages in RestoreIndex. Header
                // repair runs earlier, so it must preflight the whole replayable range.
                var complete = length - length % PAGE_SIZE;
                var prefixEnd = legacyLength.HasValue && complete != 0 ? complete : 1;
                for (var position = 0L; position < prefixEnd; position += PAGE_SIZE)
                {
                    log.Position = position;
                    log.ReadRequired(prefix, 0, prefix.Length);
                    var page = new BufferSlice(prefix, 0, prefix.Length);
                    // A torn first encrypted intent can leave fewer than a page of
                    // arbitrary plaintext. A byte at 31 alone is not a page format.
                    if (complete != 0 || IsWalPagePrefix(page))
                        EnsureLegacyWalPageFormat(page, position);
                }
                // ReadFull sees only complete legacy slots. Inspect an identifiable page
                // prefix in the partial tail before migration/checkpoint can trim it.
                var tail = length - length % PAGE_SIZE;
                if (tail != 0 && length - tail > BasePage.P_PAGE_FORMAT)
                {
                    log.Position = tail;
                    log.ReadRequired(prefix, 0, prefix.Length);
                    var page = new BufferSlice(prefix, 0, prefix.Length);
                    var type = page.ReadByte(BasePage.P_PAGE_TYPE);
                    var id = page.ReadUInt32(BasePage.P_PAGE_ID);
                    // Arbitrary garbage tails have no page header; retain legacy torn-tail
                    // recovery. A known format marker or a typed page is evidence to preserve.
                    if (prefix[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed ||
                        prefix[BasePage.P_PAGE_FORMAT] == 0xFF ||
                        (page.ReadInt64(0) == 0 && page.ReadInt64(8) == 0) ||
                        (type <= (byte)PageType.Schema && (id == 0) == (type == (byte)PageType.Header)))
                        EnsureLegacyWalPageFormat(page, tail);
                }
                if (length < WalChecksum.FrameSize) return;
                var frame = new byte[WalChecksum.FrameSize];
                log.Position = 0;
                log.ReadRequired(frame, 0, frame.Length);
                if (WalChecksum.IsFrame(frame, 0))
                    throw new LiteException(LiteException.INVALID_DATABASE, "Cannot open this database: its log file holds " +
                        "WAL frames of a converted database while its data file still has the legacy (v5) header, so the " +
                        "conversion's header never reached the device. Replaying those frames would corrupt the data file. " +
                        "Move the log file aside to open the database as it was before the conversion.");
            }
            finally { _logPool.Return(reader); }
        }

        private void RejectPreparedForeignTail(Stream stream, long start)
        {
            var prefix = new byte[BasePage.P_PAGE_FORMAT + 1];
            for (var position = start; position + prefix.Length <= stream.Length; position += PAGE_SIZE)
            {
                stream.Position = position;
                stream.ReadRequired(prefix, 0, prefix.Length);
                var page = new BufferSlice(prefix, 0, prefix.Length);
                // A conversion confirmation may tear into arbitrary plaintext,
                // especially through AES. Only a surviving page header identifies
                // a foreign format; its marker alone is not journal ownership.
                if (IsWalPagePrefix(page)) EnsureLegacyWalPageFormat(page, position);
            }
        }

        private static bool IsWalPagePrefix(BufferSlice page)
        {
            if (page.ReadInt64(0) == 0 && page.ReadInt64(8) == 0) return true;
            var type = page.ReadByte(BasePage.P_PAGE_TYPE);
            var id = page.ReadUInt32(BasePage.P_PAGE_ID);
            return type <= (byte)PageType.Schema && (id == 0) == (type == (byte)PageType.Header) &&
                page.ReadByte(BasePage.P_IS_CONFIRMED) <= 1 &&
                page.ReadUInt16(BasePage.P_USED_BYTES) <= PAGE_SIZE - PAGE_HEADER_SIZE &&
                page.ReadUInt16(BasePage.P_FRAGMENTED_BYTES) <= PAGE_SIZE - PAGE_HEADER_SIZE &&
                page.ReadUInt16(BasePage.P_NEXT_FREE_POSITION) <= PAGE_SIZE;
        }

        internal static void EnsureLegacyWalPageFormat(BufferSlice page, long position)
        {
            var format = page.ReadByte(BasePage.P_PAGE_FORMAT);
            if (format == PageChecksum.Legacy) return;
            throw new LiteException(LiteException.INVALID_DATABASE,
                "Cannot open this database: its log file holds WAL frames of a converted database " +
                "or an unsupported page format while its data file still has the legacy (v5) header. " +
                "The log page has non-legacy format 0x{0:X2} at plaintext offset {1}. " +
                "Replaying or clearing the log would lose recovery evidence. " +
                "Neither file was changed; preserve both files for recovery.", format, position + BasePage.P_PAGE_FORMAT);
        }
    }
}
