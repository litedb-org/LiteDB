using System;
using System.IO;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class StreamExtensions
    {
        /// <summary>
        /// Flush to disk through engine wrappers, using FileStream.Flush(true) at the file boundary.
        /// </summary>
        public static void FlushToDisk(this Stream stream)
        {
            if (stream is FileStream fstream)
            {
                fstream.Flush(true);
            }
            else if (stream is AesStream encrypted)
            {
                encrypted.FlushToDisk();
            }
            else if (stream is ConcurrentStream concurrent)
            {
                concurrent.FlushToDisk();
            }
            else
            {
                stream.Flush();
            }
        }
    }
}