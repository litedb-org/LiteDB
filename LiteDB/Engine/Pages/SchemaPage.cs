using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Append-only definitions. The normal snapshot/WAL owns visibility and rollback.</summary>
    internal sealed class SchemaPage : BasePage
    {
        private const uint Magic = 0x31484353; // SCH1
        private const int Start = PAGE_HEADER_SIZE + 8;
        private ushort _length;
        private ushort _count;

        internal SchemaPage(PageBuffer buffer, uint id) : base(buffer, id, PageType.Schema) { }

        internal SchemaPage(PageBuffer buffer) : base(buffer)
        {
            if (PageType != PageType.Schema || buffer.ReadUInt32(PAGE_HEADER_SIZE) != Magic)
                throw new InvalidDataException("Invalid schema page marker/type.");
            _length = buffer.ReadUInt16(PAGE_HEADER_SIZE + 4);
            _count = buffer.ReadUInt16(PAGE_HEADER_SIZE + 6);
            if (_length > PAGE_SIZE - Start || _count == 0 || _count > StorageSchema.MaxSchemas)
                throw new InvalidDataException("Invalid schema page length/count.");
        }

        internal bool CanAppend(StorageSchema schema) => Start + _length + 12 + schema.Definition.Length <= PAGE_SIZE;

        internal void Append(StorageSchema schema)
        {
            if (!CanAppend(schema)) throw new InvalidDataException("Schema exceeds page capacity.");
            using (var writer = new BufferWriter(Buffer.Slice(Start + _length, PAGE_SIZE - Start - _length)))
            {
                writer.Write(schema.Id);
                writer.Write(unchecked((long)schema.Fingerprint));
                writer.Write(schema.Definition, 0, schema.Definition.Length);
            }
            _length += (ushort)(12 + schema.Definition.Length);
            _count++;
            IsDirty = true;
        }

        internal IEnumerable<StorageSchema> ReadSchemas()
        {
            using (var stream = new MemoryStream(Buffer.Array, Buffer.Offset + Start, _length, false))
            using (var reader = new BinaryReader(stream, StorageSchema.Utf8))
            {
                for (var i = 0; i < _count; i++) yield return StorageSchema.Read(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing schema bytes.");
            }
        }

        public override PageBuffer UpdateBuffer()
        {
            if (PageType != PageType.Empty)
            {
                Buffer.Write(Magic, PAGE_HEADER_SIZE);
                Buffer.Write(_length, PAGE_HEADER_SIZE + 4);
                Buffer.Write(_count, PAGE_HEADER_SIZE + 6);
            }
            return base.UpdateBuffer();
        }
    }
}
