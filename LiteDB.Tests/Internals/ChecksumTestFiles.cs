using System;
using System.IO;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    internal static class ChecksumTestFiles
    {
        internal static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        // The v8/v9 data layout is unchanged: the new data checksum occupies an
        // unused transaction field. Remove WAL trailers to construct legacy images.
        internal static void MakeLegacy(Stream data, MemoryStream log, string password, byte version = 8)
        {
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(true, false))
            {
                var page = new byte[PAGE_SIZE];
                stream.ReadRequired(page, 0, page.Length);
                var lastPageID = new BufferSlice(page, 0, PAGE_SIZE).ReadUInt32(HeaderPage.P_LAST_PAGE_ID);
                for (long id = 0; id <= lastPageID; id++)
                {
                    stream.Position = id * PAGE_SIZE;
                    stream.ReadRequired(page, 0, page.Length);
                    page[BasePage.P_PAGE_FORMAT] = PageChecksum.Legacy;
                    if (id == 0)
                    {
                        page[HeaderPage.P_FILE_VERSION] = version;
                        new BufferSlice(page, 0, PAGE_SIZE).Write(0u, WalChecksum.MarkerPosition);
                    }
                    stream.Position = id * PAGE_SIZE;
                    stream.Write(page, 0, page.Length);
                }
            }
            if (log.Length == 0) return;
            using var source = new StreamFactory(log, password);
            using var input = source.GetStream(false, false);
            using var output = new MemoryStream();
            using (var destination = new StreamFactory(output, password))
            using (var writer = destination.GetStream(true, false))
            {
                var frame = new byte[WalChecksum.FrameSize];
                for (long position = 0; position < input.Length; position += frame.Length)
                {
                    input.Position = position;
                    input.ReadRequired(frame, 0, frame.Length);
                    frame[BasePage.P_PAGE_FORMAT] = PageChecksum.Legacy;
                    if (frame[BasePage.P_PAGE_TYPE] == (byte)PageType.Header)
                    {
                        frame[HeaderPage.P_FILE_VERSION] = version;
                        new BufferSlice(frame, 0, PAGE_SIZE).Write(0u, WalChecksum.MarkerPosition);
                    }
                    writer.Write(frame, 0, PAGE_SIZE);
                }
            }
            log.SetLength(0);
            log.Position = 0;
            var bytes = output.ToArray();
            log.Write(bytes, 0, bytes.Length);
        }
    }
}
