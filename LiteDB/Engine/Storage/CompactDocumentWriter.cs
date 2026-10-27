using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace LiteDB.Engine
{
    internal sealed class CompactDocumentWriter : IDisposable
    {
        internal const int Magic = unchecked((int)0xC14C4442);
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;
        private readonly SchemaCatalog _catalog;
        private readonly Func<ulong, bool> _admit;
        private byte[] _textBuffer;
        internal List<StorageSchema> Pending { get; } = new List<StorageSchema>();

        internal CompactDocumentWriter(SchemaCatalog catalog, Func<ulong, bool> admit = null)
        {
            _catalog = catalog;
            _admit = admit;
            _writer = new BinaryWriter(_stream, StorageSchema.Utf8, true);
        }

        // The snapshot owns this encoder. Payloads returned to write plans are independent
        // arrays; neither a safepoint nor another write can overwrite an outstanding plan.
        internal void Reset()
        {
            Pending.Clear();
            _stream.SetLength(0);
            _stream.Position = 0;
            // A single large value must not retain a multi-megabyte buffer for the transaction.
            if (_stream.Capacity > 64 * 1024) _stream.Capacity = 0;
        }

        internal byte[] Encode(BsonDocument document, int maxLength = Constants.MAX_DOCUMENT_SIZE)
        {
            Reset();
            var schema = SelectSchema(document);
            // Decide one-off scalar admission before allocating the stream's backing buffer.
            if (_admit != null && schema == null && !HasContainers(document)) return null;
            _writer.Write(Magic);
            _writer.Write((byte)1);
            _writer.Write((byte)0);
            _writer.Write(0); // exact total length filled once, after all schema choices
            WriteDocument(document, 0, schema);
            if (_stream.Length > maxLength || _stream.Length > Constants.MAX_DOCUMENT_SIZE) return null;
            var length = (int)_stream.Length;
            _stream.Position = 6;
            _writer.Write(length);
            return _stream.ToArray();
        }

        internal static bool HasContainers(BsonDocument document)
        {
            foreach (var element in new CompactDocumentElements(document))
                if (element.Value.IsArray || element.Value.IsDocument) return true;
            return false;
        }

        private StorageSchema SelectSchema(BsonDocument document)
        {
            if (document.Count > ushort.MaxValue) throw new NotSupportedException("Compact field count limit.");
            return _catalog.Select(document, Pending, _admit);
        }

        private void WriteName(string name)
        {
            var length = StorageSchema.Utf8.GetByteCount(name);
            if (length > ushort.MaxValue || name.IndexOf('\0') >= 0) throw new NotSupportedException("Compact field name limit.");
            _writer.Write((ushort)length);
            WriteText(name, length);
        }

        private void WriteText(string text, int length)
        {
            if (length == 0) return;
            var bytes = length > 256 ? ArrayPool<byte>.Shared.Rent(length) : (_textBuffer ??= new byte[256]);
            try
            {
                StorageSchema.Utf8.GetBytes(text, 0, text.Length, bytes, 0);
                _writer.Write(bytes, 0, length);
            }
            finally
            {
                if (length > 256) ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        private void WriteDocument(BsonDocument document, int depth, StorageSchema schema)
        {
            if (depth > 64) throw new NotSupportedException("Compact nesting limit.");
            _writer.Write(schema?.Id ?? 0);
            if (schema == null)
            {
                _writer.Write((ushort)document.Count);
                foreach (var element in new CompactDocumentElements(document))
                {
                    WriteName(element.Key);
                    WriteValue(element.Value, depth + 1);
                }
            }
            else
            {
                _writer.Write(schema.Fingerprint);
                var elements = new CompactDocumentElements(document).GetEnumerator();
                var hasNext = elements.MoveNext();
                byte bitmap = 0;
                for (var slot = 0; slot < schema.Fields.Length; slot++)
                {
                    if (hasNext && schema.Fields[slot] == elements.Current.Key)
                    {
                        bitmap |= (byte)(1 << (slot % 8));
                        hasNext = elements.MoveNext();
                    }
                    if (slot % 8 == 7 || slot == schema.Fields.Length - 1)
                    {
                        _writer.Write(bitmap);
                        bitmap = 0;
                    }
                }
                foreach (var element in new CompactDocumentElements(document)) WriteValue(element.Value, depth + 1);
            }
        }

        private void WriteValue(BsonValue value, int depth)
        {
            if (depth > 64) throw new NotSupportedException("Compact nesting limit.");
            _writer.Write((byte)value.Type);
            switch (value.Type)
            {
                case BsonType.Document: WriteDocument(value.AsDocument, depth, SelectSchema(value.AsDocument)); break;
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
                    var text = value.AsString;
                    var length = StorageSchema.Utf8.GetByteCount(text);
                    _writer.Write(length);
                    WriteText(text, length);
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
