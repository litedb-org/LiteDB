using System;
using System.Collections.Generic;
using System.IO;

namespace LiteDB.Engine
{
    internal sealed class CompactDocumentReader : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;
        private readonly Func<SchemaCatalog> _catalog;
        private readonly bool _utcDate;
        internal uint SchemaId { get; private set; }

        internal CompactDocumentReader(byte[] payload, Func<SchemaCatalog> catalog, bool utcDate)
        {
            _stream = new MemoryStream(payload, false);
            _reader = new BinaryReader(_stream, StorageSchema.Utf8);
            _catalog = catalog;
            _utcDate = utcDate;
        }

        internal BsonDocument Decode(HashSet<string> fields)
        {
            var result = ReadDocument(0);
            if (_stream.Position != _stream.Length) throw new InvalidDataException("Trailing compact bytes.");
            if (fields != null && fields.Count > 0)
            {
                var selected = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
                var filtered = new BsonDocument();
                foreach (var element in result.GetElements()) if (selected.Contains(element.Key)) filtered.Add(element.Key, element.Value);
                return filtered;
            }
            return result;
        }

        private int Bound(int count, int minimumBytes = 1)
        {
            if (count < 0 || count > (_stream.Length - _stream.Position) / minimumBytes)
                throw new InvalidDataException("Compact length/count exceeds payload.");
            return count;
        }

        private byte[] Bytes(int count)
        {
            Bound(count);
            var bytes = _reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return bytes;
        }

        private string Name()
        {
            var name = StorageSchema.Utf8.GetString(Bytes(_reader.ReadUInt16()));
            if (name.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid compact field name.");
            return name;
        }

        private BsonDocument ReadDocument(int depth)
        {
            if (depth > 64) throw new InvalidDataException("Compact nesting limit.");
            var result = new BsonDocument();
            var id = _reader.ReadUInt32();
            if (id == 0)
            {
                var count = Bound(_reader.ReadUInt16(), 3);
                for (var i = 0; i < count; i++) Add(result, Name(), ReadValue(depth + 1));
            }
            else
            {
                SchemaId = id;
                var schema = _catalog().Get(id, _reader.ReadUInt64());
                var bitmap = Bytes((schema.Fields.Length + 7) / 8);
                var unused = schema.Fields.Length % 8;
                if (unused != 0 && (bitmap[bitmap.Length - 1] >> unused) != 0) throw new InvalidDataException("Invalid presence bitmap.");
                for (var i = 0; i < schema.Fields.Length; i++)
                {
                    if ((bitmap[i / 8] & (1 << (i % 8))) != 0) Add(result, schema.Fields[i], ReadValue(depth + 1));
                }
            }
            return result;
        }

        private static void Add(BsonDocument document, string name, BsonValue value)
        {
            if (document.ContainsKey(name)) throw new InvalidDataException("Duplicate compact field.");
            document.Add(name, value);
        }

        private BsonValue ReadValue(int depth)
        {
            if (depth > 64) throw new InvalidDataException("Compact nesting limit.");
            var type = (BsonType)_reader.ReadByte();
            switch (type)
            {
                case BsonType.Document: return ReadDocument(depth);
                case BsonType.Array:
                    var count = Bound(_reader.ReadInt32());
                    var array = new BsonArray();
                    for (var i = 0; i < count; i++) array.Add(ReadValue(depth + 1));
                    return array;
                case BsonType.Int32: return _reader.ReadInt32();
                case BsonType.Int64: return _reader.ReadInt64();
                case BsonType.Double: return _reader.ReadDouble();
                case BsonType.Decimal: return new decimal(new[] { _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32() });
                case BsonType.String: return StorageSchema.Utf8.GetString(Bytes(_reader.ReadInt32()));
                case BsonType.Binary: return Bytes(_reader.ReadInt32());
                case BsonType.Guid: return new Guid(Bytes(16));
                case BsonType.ObjectId: return new ObjectId(Bytes(12));
                case BsonType.Boolean:
                    var boolean = _reader.ReadByte();
                    if (boolean > 1) throw new InvalidDataException("Invalid compact boolean.");
                    return boolean == 1;
                case BsonType.DateTime:
                    var ms = _reader.ReadInt64();
                    if (ms >= 253402300800000) return DateTime.MaxValue;
                    if (ms <= -62135596800000) return DateTime.MinValue;
                    var date = BsonValue.UnixEpoch.AddMilliseconds(ms);
                    return _utcDate ? date : date.ToLocalTime();
                case BsonType.Vector:
                    var dimensions = Bound(_reader.ReadUInt16(), 4);
                    var vector = new float[dimensions];
                    for (var i = 0; i < dimensions; i++) vector[i] = _reader.ReadSingle();
                    return new BsonVector(vector);
                case BsonType.Null: return BsonValue.Null;
                case BsonType.MinValue: return BsonValue.MinValue;
                case BsonType.MaxValue: return BsonValue.MaxValue;
                default: throw new InvalidDataException("Unknown compact value type.");
            }
        }

        public void Dispose() => _reader.Dispose();
    }
}
