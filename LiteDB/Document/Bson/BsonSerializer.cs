using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
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
        /// Serialize BsonDocument into a binary array
        /// </summary>
        public static byte[] Serialize(BsonDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var buffer = new byte[doc.GetBytesCount(true)]; 

            using (var writer = new BufferWriter(buffer))
            {
                writer.WriteDocument(doc, false);
            }

            return buffer;
        }

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