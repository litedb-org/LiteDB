using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private HeaderPage _initialHeader;

        /// <summary>Transfer the header already validated during this disk open.</summary>
        internal HeaderPage ReadHeader()
        {
            var header = _initialHeader;
            _initialHeader = null;
            return header ?? new HeaderPage(this.ReadFull(FileOrigin.Data).First());
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
                return new HeaderPage(new PageBuffer(bytes, 0, 0)
                {
                    Position = 0,
                    Origin = FileOrigin.Data
                });
            }
            finally
            {
                _dataPool.Return(stream);
            }
        }

    }
}
