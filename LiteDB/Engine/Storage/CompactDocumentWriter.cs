using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class CompactDocumentWriter : IDisposable
    {
        internal const int Magic = unchecked((int)0xC14C4442);
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;
        private readonly SchemaCatalog _catalog;
        private readonly Func<ulong, bool> _admit;
        internal List<StorageSchema> Pending { get; } = new List<StorageSchema>();

        internal CompactDocumentWriter(SchemaCatalog catalog, Func<ulong, bool> admit = null)
        {
            _catalog = catalog;
            _admit = admit;
            _writer = new BinaryWriter(_stream, StorageSchema.Utf8, true);
        }

        internal byte[] Encode(BsonDocument document)
        {
            _writer.Write(Magic);
            _writer.Write((byte)1);
            _writer.Write((byte)0);
            _writer.Write(0); // exact total length filled once, after all schema choices
            if (!WriteDocument(document, 0)) return null;
            if (_stream.Length > Constants.MAX_DOCUMENT_SIZE) return null;
            var length = (int)_stream.Length;
            _stream.Position = 6;
            _writer.Write(length);
            return _stream.ToArray();
        }

        private void WriteName(string name)
        {
            var bytes = StorageSchema.Utf8.GetBytes(name);
            if (bytes.Length > ushort.MaxValue || name.IndexOf('\0') >= 0) throw new NotSupportedException("Compact field name limit.");
            _writer.Write((ushort)bytes.Length);
            _writer.Write(bytes);
        }

        private bool WriteDocument(BsonDocument document, int depth)
        {
            if (depth > 64) throw new NotSupportedException("Compact nesting limit.");
            var elements = document.GetElements().ToArray();
            if (elements.Length > ushort.MaxValue) throw new NotSupportedException("Compact field count limit.");
            var fields = elements.Select(e => e.Key).ToArray();
            var schema = _catalog.Select(fields, Pending, _admit);
            // Dynamic scalar dictionaries cannot save space without a reused shape.
            // Avoid encoding/copying every value merely to reject the result later.
            if (_admit != null && depth == 0 && schema == null && !elements.Any(e => e.Value.IsArray || e.Value.IsDocument)) return false;
            _writer.Write(schema?.Id ?? 0);
            if (schema == null)
            {
                _writer.Write((ushort)elements.Length);
                foreach (var element in elements)
                {
                    WriteName(element.Key);
                    WriteValue(element.Value, depth + 1);
                }
            }
            else
            {
                _writer.Write(schema.Fingerprint);
                var bitmap = new byte[(schema.Fields.Length + 7) / 8];
                var next = 0;
                for (var slot = 0; slot < schema.Fields.Length; slot++)
                {
                    if (next < fields.Length && schema.Fields[slot] == fields[next])
                    {
                        bitmap[slot / 8] |= (byte)(1 << (slot % 8));
                        next++;
                    }
                }
                _writer.Write(bitmap);
                foreach (var element in elements) WriteValue(element.Value, depth + 1);
            }
            return true;
        }

        private void WriteValue(BsonValue value, int depth)
        {
            if (depth > 64) throw new NotSupportedException("Compact nesting limit.");
            _writer.Write((byte)value.Type);
            switch (value.Type)
            {
                case BsonType.Document: WriteDocument(value.AsDocument, depth); break;
                case BsonType.Array:
                    _writer.Write(value.AsArray.Count);
                    foreach (var item in value.AsArray) WriteValue(item, depth + 1);
                    break;
                case BsonType.Int32: _writer.Write(value.AsInt32); break;
                case BsonType.Int64: _writer.Write(value.AsInt64); break;
                case BsonType.Double: _writer.Write(value.AsDouble); break;
                case BsonType.Decimal:
                    foreach (var part in decimal.GetBits(value.AsDecimal)) _writer.Write(part);
                    break;
                case BsonType.String:
                    var text = StorageSchema.Utf8.GetBytes(value.AsString);
                    _writer.Write(text.Length);
                    _writer.Write(text);
                    break;
                case BsonType.Binary:
                    _writer.Write(value.AsBinary.Length);
                    _writer.Write(value.AsBinary);
                    break;
                case BsonType.Guid: _writer.Write(value.AsGuid.ToByteArray()); break;
                case BsonType.ObjectId: _writer.Write(value.AsObjectId.ToByteArray()); break;
                case BsonType.Boolean: _writer.Write(value.AsBoolean); break;
                case BsonType.DateTime:
                    var date = value.AsDateTime;
                    var utc = date == DateTime.MinValue || date == DateTime.MaxValue ? date : date.ToUniversalTime();
                    _writer.Write(Convert.ToInt64((utc - BsonValue.UnixEpoch).TotalMilliseconds));
                    break;
                case BsonType.Vector:
                    Constants.ENSURE(value.AsVector.Length <= ushort.MaxValue, "Vector length must fit into UInt16");
                    _writer.Write((ushort)value.AsVector.Length);
                    foreach (var item in value.AsVector) _writer.Write(item);
                    break;
                case BsonType.Null:
                case BsonType.MinValue:
                case BsonType.MaxValue: break;
                default: throw new NotSupportedException("Unsupported compact value.");
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
