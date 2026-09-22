using System;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class HeaderJournal
    {
        private static uint ComputeVerifiedBody(Stream stream, long length, byte[] header, WalChecksum expected)
        {
            if (length % WalChecksum.FrameSize != 0) throw new PageChecksumException(FileOrigin.Log, length);
            var salt = new byte[16];
            Buffer.BlockCopy(header, WalChecksum.SaltPosition, salt, 0, salt.Length);
            var checksums = new WalChecksum();
            checksums.Reset(salt);
            var crc = uint.MaxValue;

            // Bind the exact bytes validated in this pass. A separate raw CRC
            // pass could seal damage that appeared after checkpoint preflight.
            IEnumerable<PageBuffer> Frames()
            {
                var bytes = new byte[WalChecksum.FrameSize];
                stream.Position = 0;
                for (long position = 0; position < length; position += bytes.Length)
                {
                    stream.ReadRequired(bytes, 0, bytes.Length);
                    crc = Crc32C.Update(crc, bytes, 0, bytes.Length);
                    var page = new PageBuffer(bytes, 0, 0)
                    {
                        Position = position / WalChecksum.FrameSize * PAGE_SIZE,
                        Origin = FileOrigin.Log
                    };
                    page.WalFrame = checksums.Validate(page, new BufferSlice(bytes, PAGE_SIZE, WalChecksum.MetadataSize), page.Position);
                    yield return page;
                }
            }

            var recovery = new WalRecovery();
            foreach (var page in recovery.Read(Frames())) { }
            if (recovery.InvalidTail || recovery.Sequence != expected.Sequence ||
                recovery.ConfirmedEnd != expected.LastConfirmedPosition + PAGE_SIZE)
                throw new PageChecksumException(FileOrigin.Log, recovery.ConfirmedEnd);
            return ~crc;
        }
    }
}
