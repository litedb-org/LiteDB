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
                return new HeaderPage(new PageBuffer(bytes, 0, 0));
            }
            finally
            {
                _dataPool.Return(stream);
            }
        }

    }
}
