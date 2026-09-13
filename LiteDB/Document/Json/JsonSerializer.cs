using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Static class for serialize/deserialize BsonDocuments into json extended format
    /// </summary>
    public class JsonSerializer
    {
        #region Serialize

        /// <summary>
        /// Json serialize a BsonValue into a String
        /// </summary>
        public static string Serialize(BsonValue value, bool indent = false)
        {
            var sb = new StringBuilder();

            Serialize(value, sb, indent);

            return sb.ToString();
        }

        /// <summary>
        /// Json serialize a BsonValue into a TextWriter
        /// </summary>
        public static void Serialize(BsonValue value, TextWriter writer, bool indent = false)
        {
            var json = new JsonWriter(writer)
            {
                Pretty = indent
            };

            json.Serialize(value ?? BsonValue.Null);
        }

        /// <summary>
        /// Json serialize a BsonValue into a StringBuilder
        /// </summary>
        public static void Serialize(BsonValue value, StringBuilder sb, bool indent = false)
        {
            using (var writer = new StringWriter(sb))
            {
                var w = new JsonWriter(writer)
                {
                    Pretty = indent
                };

                w.Serialize(value ?? BsonValue.Null);
            }
        }

        #endregion

        #region Deserialize

        /// <summary>
        /// Deserialize a Json string into a BsonValue
        /// </summary>
        public static BsonValue Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            using (var sr = new StringReader(json))
            {
                var reader = new JsonReader(sr);

                return reader.Deserialize();
            }
        }

        /// <summary>
        /// Deserialize a Json TextReader into a BsonValue
        /// </summary>
        public static BsonValue Deserialize(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var jr = new JsonReader(reader);

            return jr.Deserialize();
        }

        /// <summary>
        /// Deserialize a json array as an IEnumerable of BsonValue
        /// </summary>
        public static IEnumerable<BsonValue> DeserializeArray(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            var sr = new StringReader(json);
            var reader = new JsonReader(sr);
            return reader.DeserializeArray();
        }

        /// <summary>
        /// Deserialize a json array as an IEnumerable of BsonValue reading on demand TextReader
        /// </summary>
        public static IEnumerable<BsonValue> DeserializeArray(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var jr = new JsonReader(reader);

            return jr.DeserializeArray();
        }

        #endregion
    }
}