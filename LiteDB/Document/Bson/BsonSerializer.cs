using LiteDB.Engine;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Class to call method for convert BsonDocument to/from byte[] - based on http://bsonspec.org/spec.html
    /// In v5 this class use new BufferRead/Writer to work with byte[] segments. This class are just a shortchut
    /// </summary>
    public class BsonSerializer
    {
        /// <summary>
        /// Serialize <paramref name="doc"/> into a binary array.
        /// </summary>
        public static byte[] Serialize(BsonDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var bytesRequired = doc.GetBytesCount(true);
            var buffer = new byte[bytesRequired];

            Serialize(doc, buffer.AsMemory());

            return buffer;
        }

        /// <summary>
        /// Serialize <paramref name="doc"/> into the provided <see cref="Memory{T}"/>.
        /// Returns the number of bytes written to <paramref name="destination"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="doc"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is shorter than the serialized document.</exception>
        /// <exception cref="NotSupportedException">Thrown when <paramref name="destination"/> is not backed by a managed array.</exception>
        public static int Serialize(BsonDocument doc, Memory<byte> destination)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var bytesRequired = doc.GetBytesCount(true);

            if (destination.Length < bytesRequired)
            {
                throw new ArgumentException($"Destination memory must be at least {bytesRequired} bytes long", nameof(destination));
            }

            if (!MemoryMarshal.TryGetArray(destination, out ArraySegment<byte> segment))
            {
                throw new NotSupportedException("The destination memory must be backed by a managed array.");
            }

            using (var writer = new BufferWriter(new BufferSlice(segment.Array, segment.Offset, bytesRequired)))
            {
                writer.WriteDocument(doc, false);
            }

            return bytesRequired;
        }

#if !NETSTANDARD2_0
        /// <summary>
        /// Serialize <paramref name="doc"/> into the supplied <see cref="IBufferWriter{T}"/>.
        /// Returns the number of bytes written to <paramref name="writer"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="doc"/> or <paramref name="writer"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when the provided <paramref name="writer"/> exposes a buffer that is shorter than the serialized document.</exception>
        /// <exception cref="NotSupportedException">Thrown when the buffer provided by <paramref name="writer"/> is not backed by a managed array.</exception>
        public static int Serialize(BsonDocument doc, IBufferWriter<byte> writer)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            var bytesRequired = doc.GetBytesCount(true);

            var memory = writer.GetMemory(bytesRequired);
            var written = Serialize(doc, memory);

            writer.Advance(written);

            return written;
        }
#endif

        /// <summary>
        /// Deserialize binary data into BsonDocument
        /// </summary>
        public static BsonDocument Deserialize(byte[] buffer, bool utcDate = false, HashSet<string> fields = null)
        {
            if (buffer == null || buffer.Length == 0) throw new ArgumentNullException(nameof(buffer));

            using (var reader = new BufferReader(buffer, utcDate))
            {
                return reader.ReadDocument(fields).GetValue();
            }
        }
    }
}
