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
            if (!ChecksumsEnabled) throw new InvalidOperationException("Enable checksums before promoting index storage.");
            var writer = _writer.Value;
            lock (writer)
            {
                var stream = _dataPool.Writer.Value;
                lock (stream)
                {
                    var header = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
                    stream.Position = 0;
                    stream.ReadRequired(header.Array, 0, PAGE_SIZE);
                    PageChecksum.Validate(header, 0);
                    _ = new HeaderPage(header);
                    var rawLog = ((ChecksummedWalStream)writer).RawStream;
                    var originalLength = rawLog.Length;
                    BeginHeaderJournal(header.Array);
                    header[HeaderPage.P_FILE_VERSION] = version;
                    PageChecksum.Write(header);
                    stream.Position = 0;
                    stream.Write(header.Array, 0, PAGE_SIZE);
                    stream.FlushToDisk();
                    rawLog.SetLength(originalLength);
                    rawLog.FlushToDisk();
                    _checksums.JournalBytes = 0;
                    FileVersion = version;
                }
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
