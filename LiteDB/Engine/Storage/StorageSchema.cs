using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB.Engine
{
    internal sealed class StorageSchema
    {
        internal const int MaxSchemas = 256;
        internal const int MaxFields = 128;
        internal const int MaxNameBytes = 512;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal uint Id { get; }
        internal string[] Fields { get; }
        internal ulong Fingerprint { get; }
        internal byte[] Definition { get; }

        internal StorageSchema(uint id, string[] fields)
        {
            Id = id;
            Fields = fields;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write((ushort)fields.Length);
                foreach (var field in fields)
                {
                    var bytes = Utf8.GetBytes(field);
                    if (bytes.Length > MaxNameBytes || field.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid schema field.");
                    writer.Write((ushort)bytes.Length);
                    writer.Write(bytes);
                }
                Definition = stream.ToArray();
            }
            Fingerprint = Hash(Definition);
        }

        internal static ulong Hash(byte[] bytes)
        {
            // FNV-1a over the length-prefixed, ordered UTF-8 definition (not runtime string hashes).
            var hash = 14695981039346656037UL;
            foreach (var value in bytes) hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        internal bool Matches(string[] fields)
        {
            var next = 0;
            foreach (var field in Fields)
            {
                if (next < fields.Length && field == fields[next]) next++;
            }
            return next == fields.Length;
        }

        internal static StorageSchema Read(BinaryReader reader)
        {
            var id = reader.ReadUInt32();
            var fingerprint = reader.ReadUInt64();
            var count = reader.ReadUInt16();
            if (id == 0 || id > MaxSchemas || count == 0 || count > MaxFields) throw new InvalidDataException("Invalid schema id/count.");
            var fields = new string[count];
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var length = reader.ReadUInt16();
                if (length > MaxNameBytes) throw new InvalidDataException("Invalid schema name length.");
                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                fields[i] = Utf8.GetString(bytes);
                if (!unique.Add(fields[i])) throw new InvalidDataException("Duplicate schema field.");
            }
            var schema = new StorageSchema(id, fields);
            if (schema.Fingerprint != fingerprint) throw new InvalidDataException("Schema fingerprint mismatch.");
            return schema;
        }
    }
}
