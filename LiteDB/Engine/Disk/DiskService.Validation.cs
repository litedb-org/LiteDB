using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // The header ValidateExistingData read, recovered and validated; nothing can write
        // the header before LiteEngine.Open takes it (same engine, still opening).
        private HeaderPage _openingHeader;

        /// <summary>
        /// The opening header already read, parsed and validated by this disk service;
        /// null after transfer (and for new files). No bytes survive a cold reopen.
        /// </summary>
        internal HeaderPage TakeOpeningHeader()
        {
            var header = _openingHeader;
            _openingHeader = null;
            return header;
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
                // Read-only journal recovery retains an immutable redo image for
                // disk reads. WAL replay may mutate the engine header in place.
                var opening = ReferenceEquals(bytes, _recoveredHeader) ? (byte[])bytes.Clone() : bytes;
                _openingHeader = new HeaderPage(new PageBuffer(opening, 0, 0) { Position = 0, Origin = FileOrigin.Data, ShareCounter = 0 });
                // Only a valid legacy header can meet the WAL of its own conversion.
                if (!ChecksumsEnabled) this.RejectConvertedWal();
                return _openingHeader;
            }
            finally
            {
                _dataPool.Return(stream);
            }
        }

        /// <summary>
        /// A legacy header next to a WAL of checksummed frames: a conversion's header never reached
        /// the device while frames written after it did (storage that cannot sync, #2242, or a data
        /// file restored without its log). Legacy rules would replay those frames as pages at
        /// positions read from their trailers, so refuse the open; it changes neither file. A converted
        /// WAL starts over at its first frame, whose trailer lands on a legacy page's page ID, which
        /// no document controls; later frames overlap legacy page content, so only the first is read.
        /// </summary>
        private void RejectConvertedWal()
        {
            // Like the header journal, never open a log shorter than a frame: an interrupted
            // encrypted preamble must stay as it is until recovery completes it.
            if (!_logFactory.Exists() || _logFactory.GetLength() < WalChecksum.FrameSize) return;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                var log = reader.RawStream;
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
