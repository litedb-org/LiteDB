using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class StreamExtensions
    {
        /// <summary>
        /// If Stream are FileStream, flush content direct to disk (avoid OS cache)
        /// </summary>
        public static void FlushToDisk(this Stream stream)
        {
            if (stream is FileStream fstream)
            {
                fstream.Flush(true);
            }
            else
            {
                stream.Flush();
            }
        }

        /// <summary>
        /// Flushes the stream contents to disk asynchronously, avoiding OS level buffering when possible.
        /// </summary>
        public static ValueTask FlushToDiskAsync(this Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream is FileStream fstream)
            {
                fstream.Flush(true);
                return default;
            }

            return new ValueTask(stream.FlushAsync(cancellationToken));
        }
    }
}