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

                this.RecoverHeaderJournal(ref bytes);
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
        /// no document controls; later frames overlap legacy page content, so only the first is read.
        /// </summary>
        private void RejectConvertedWal()
        {
            // GetLength is plaintext length: an incomplete encrypted preamble has no content
            // and must stay untouched. A page-format marker needs only the first 32 bytes,
            // even when the payload or its checksum trailer was torn.
            if (!_logFactory.Exists()) return;
            var length = _logFactory.GetLength();
            if (length <= BasePage.P_PAGE_FORMAT) return;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                var log = reader.RawStream;
                var prefix = new byte[BasePage.P_PAGE_FORMAT + 1];
                log.Position = 0;
                log.ReadRequired(prefix, 0, prefix.Length);
                if (prefix[BasePage.P_PAGE_FORMAT] != PageChecksum.Legacy)
                    throw new LiteException(LiteException.INVALID_DATABASE, "Cannot open this database: its log file holds WAL frames of a converted database " +
                        "or an unsupported page format while its data file still has the legacy (v5) header. " +
                        "The first log page has non-legacy format 0x{0:X2} at plaintext offset {1}. " +
                        "Replaying it would corrupt the data file. Neither file was changed; preserve both files for recovery.",
                        prefix[BasePage.P_PAGE_FORMAT], BasePage.P_PAGE_FORMAT);
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
    }
}
