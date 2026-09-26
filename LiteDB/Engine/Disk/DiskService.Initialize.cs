using System;
using System.IO;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Create a new empty database (use synced mode)
        /// </summary>
        private void Initialize(Stream stream, Collation collation, long initialSize, bool compactByDefault)
        {
            if (initialSize > 0)
            {
                if (stream is AesStream) throw LiteException.InitialSizeCryptoNotSupported();
                if (initialSize % PAGE_SIZE != 0) throw LiteException.InvalidInitialSize();
            }

            _signals?.SlotReused();
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var header = new HeaderPage(buffer, 0);

            if (compactByDefault)
            {
                header.EnsureVersion(HeaderPage.COMPACT_FILE_VERSION);
            }

            header.Pragmas.Set(Pragmas.COLLATION, (collation ?? Collation.Default).ToString(), false);
            header.UpdateBuffer();
            _checksums.Reset(Guid.NewGuid().ToByteArray());
            FileVersion = header.FileVersion;
            this.StampDataPage(buffer);
            stream.Write(buffer.Array, buffer.Offset, PAGE_SIZE);

            if (initialSize > 0)
            {
                stream.SetLength(initialSize);
            }

            stream.FlushToDisk();
        }
    }
}
