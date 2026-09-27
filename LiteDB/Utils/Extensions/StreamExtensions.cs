using System;
using System.IO;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class StreamExtensions
    {
        /// <summary>
        /// Fill a buffer across short reads, stopping only at the requested count or EOF.
        /// </summary>
        public static int ReadFully(this Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        public static void ReadRequired(this Stream stream, byte[] buffer, int offset, int count)
        {
            if (stream.ReadFully(buffer, offset, count) != count)
            {
                throw new EndOfStreamException("The stream ended before the requested data was read.");
            }
        }

        /// <summary>
        /// Flush to disk through engine wrappers. At the file boundary, <see cref="NativeFileSync"/>
        /// syncs the descriptor so that Unix sync failures are reported (FileStream.Flush(true) loses them).
        /// </summary>
        public static void FlushToDisk(this Stream stream)
        {
            if (stream is FileStream fstream)
            {
                NativeFileSync.FlushToDisk(fstream);
            }
            else if (stream is AesStream encrypted)
            {
                encrypted.FlushToDisk();
            }
            else if (stream is ConcurrentStream concurrent)
            {
                concurrent.FlushToDisk();
            }
            else if (stream is ChecksummedWalStream wal)
            {
                wal.FlushToDisk();
            }
#if DEBUG || TESTING
            else if (stream is IDurableStream durable)
            {
                durable.FlushToDisk();
            }
#endif
            else
            {
                stream.Flush();
            }
        }
    }
}
