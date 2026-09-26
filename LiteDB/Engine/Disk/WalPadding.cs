using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Released v5 engines round a WAL file down to an 8 KiB boundary before
    /// checking the data-file version, even on read-only open. Keep acknowledged
    /// modern frames below that boundary. Padding is never a logical WAL frame.
    /// </summary>
    internal static class WalPadding
    {
        internal static long AlignedLength(long frameBytes) =>
            frameBytes == 0 ? 0 : checked((frameBytes + PAGE_SIZE - 1) / PAGE_SIZE * PAGE_SIZE);

        internal static long TrailingBytes(long length)
        {
            var trailing = length % WalChecksum.FrameSize;
            return length % PAGE_SIZE == 0 && trailing < PAGE_SIZE ? 0 : trailing;
        }

        internal static void Pad(Stream stream, long frameBytes, bool initialize = false)
        {
            var length = AlignedLength(frameBytes);
            stream.SetLength(length);
            if (initialize && length != frameBytes)
            {
                // AesStream normalizes a read starting with zero ciphertext to
                // a blank page. Materialize plaintext padding before hashing a
                // journal body so CRC does not depend on read chunk boundaries.
                stream.Position = frameBytes;
                var padding = new byte[length - frameBytes];
                stream.Write(padding, 0, padding.Length);
            }
        }
    }
}
