using System;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class CollectionIndex
    {
        /// <summary>
        /// Slot index [0-255] used in all index nodes
        /// </summary>
        public byte Slot { get; }

        /// <summary>
        /// Indicate index type: 0 = SkipList (reserved for future use)
        /// </summary>
        public byte IndexType { get; }

        /// <summary>
        /// Index name
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Get index expression (path or expr)
        /// </summary>
        public string Expression { get; }

        /// <summary>
        /// Get BsonExpression from Expression
        /// </summary>
        public BsonExpression BsonExpr { get; }

        /// <summary>
        /// Indicate if this index has distinct values only
        /// </summary>
        public bool Unique { get; }

        /// <summary>
        /// Head page address for this index
        /// </summary>
        public PageAddress Head { get; set; }

        /// <summary>
        /// A link pointer to tail node
        /// </summary>
        public PageAddress Tail { get; set; }

        /// <summary>
        /// Reserved byte (old max level)
        /// </summary>
        public byte Reserved { get; set; } = 1;

        /// <summary>
        /// Free index page linked-list (all pages here must have at least 600 bytes)
        /// </summary>
        public uint FreeIndexPageList;

        /// <summary>
        /// Returns if this index slot is empty and can be used as new index
        /// </summary>
        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(this.Name); }
        }

        public CollectionIndex(byte slot, byte indexType, string name, string expr, bool unique)
        {
            this.Slot = slot;
            this.IndexType = indexType;
            this.Name = name;
            this.Expression = expr;
            this.Unique = unique;
            this.FreeIndexPageList = uint.MaxValue;

            this.BsonExpr = BsonExpression.Create(expr);
        }

        public CollectionIndex(BufferReader reader)
        {
            this.Slot = reader.ReadByte();
            this.IndexType = reader.ReadByte();
            this.Name = reader.ReadCString();
            this.Expression = reader.ReadCString();
            this.Unique = reader.ReadBoolean();
            this.Head = reader.ReadPageAddress(); // 5
            this.Tail = reader.ReadPageAddress(); // 5
            this.Reserved = reader.ReadByte(); // 1
            this.FreeIndexPageList = reader.ReadUInt32(); // 4

            this.BsonExpr = BsonExpression.Create(this.Expression);
        }

        public void UpdateBuffer(BufferWriter writer)
        {
            writer.Write(this.Slot);
            writer.Write(this.IndexType);
            writer.WriteCString(this.Name);
            writer.WriteCString(this.Expression);
            writer.Write(this.Unique);
            writer.Write(this.Head);
            writer.Write(this.Tail);
            writer.Write(this.Reserved);
            writer.Write(this.FreeIndexPageList);
        }

        /// <summary>
        /// Get index collection size used in CollectionPage
        /// </summary>
        public static int GetLength(CollectionIndex index)
        {
            return GetLength(index.Name, index.Expression);
        }

        /// <summary>
        /// Get index collection size used in CollectionPage
        /// </summary>
        public static int GetLength(string name, string expr)
        {
            return
                1 + // Slot
                1 + // IndexType
                StringEncoding.UTF8.GetByteCount(name) + 1 + // Name + \0
                StringEncoding.UTF8.GetByteCount(expr) + 1 + // Expression + \0
                1 + // Unique
                PageAddress.SIZE + // Head
                PageAddress.SIZE + // Tail
                1 + // MaxLevel
                4; // FreeListPage
        }
    }
}