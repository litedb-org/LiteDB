using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        internal byte FileVersion { get; set; } = HeaderPage.FILE_VERSION;

        /// <summary>
        /// Publish the vector format before any vector bytes can enter the WAL.
        /// </summary>
        internal void PromoteVectorFormat() => this.PromoteFileFormat(HeaderPage.VECTOR_FILE_VERSION);

        /// <summary>
        /// Writes hold the header lock and an active transaction; startup migration
        /// owns the disk exclusively. Only the persisted header is copied, never
        /// uncommitted header fields. Flush before publishing dependent WAL pages.
        /// </summary>
        internal void PromoteFileFormat(byte version)
        {
            if (FileVersion >= version) return;
            var stream = _dataPool.Writer.Value;
            lock (stream)
            {
                var header = new byte[PAGE_SIZE];
                stream.Position = 0;
                var read = 0;
                while (read < header.Length)
                {
                    var count = stream.Read(header, read, header.Length - read);
                    if (count == 0) throw new EndOfStreamException("Cannot promote an incomplete database header.");
                    read += count;
                }
                if (header[HeaderPage.P_FILE_VERSION] != HeaderPage.FILE_VERSION &&
                    header[HeaderPage.P_FILE_VERSION] != HeaderPage.VECTOR_FILE_VERSION &&
                    header[HeaderPage.P_FILE_VERSION] != HeaderPage.INDEX_FILE_VERSION)
                {
                    throw LiteException.UnsupportedFileVersion(header[HeaderPage.P_FILE_VERSION]);
                }
                header[HeaderPage.P_FILE_VERSION] = version;
                stream.Position = 0;
                // A full page also works with encrypted streams; all other header fields are preserved.
                stream.Write(header, 0, header.Length);
                stream.FlushToDisk();
                FileVersion = version;
            }
        }

        private void PreserveFileVersion(PageBuffer page)
        {
            // Both write paths own this writable/uncached page; no shared read frame is modified.
            if (page.ReadUInt32(BasePage.P_PAGE_ID) == 0 && page.ReadByte(BasePage.P_PAGE_TYPE) == (byte)PageType.Header)
            {
                page[HeaderPage.P_FILE_VERSION] = Math.Max(page[HeaderPage.P_FILE_VERSION], FileVersion);
            }
        }
    }
}
