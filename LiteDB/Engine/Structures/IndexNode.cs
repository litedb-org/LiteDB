using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent a index node inside a Index Page
    /// </summary>
    internal class IndexNode
    {
        /// <summary>
        /// Fixed length of IndexNode (12 bytes)
        /// </summary>
        private const int INDEX_NODE_FIXED_SIZE = 1 + // Slot [1 byte]
                                                  1 + // Levels [1 byte]
                                                  PageAddress.SIZE + // DataBlock (5 bytes)
                                                  PageAddress.SIZE; // NextNode (5 bytes)

        private const int P_SLOT = 0; // 00-00 [byte]
        private const int P_LEVELS = 1; // 01-01 [byte]
        private const int P_DATA_BLOCK = 2; // 02-06 [PageAddress]
        private const int P_NEXT_NODE = 7; // 07-11 [PageAddress]
        private const int P_PREV_NEXT = 12; // 12-(_level * 5 [PageAddress] * 2 [prev-next])
        private int P_KEY => P_PREV_NEXT + (this.Levels * PageAddress.SIZE * 2); // just after NEXT

        private readonly IndexPage _page;
        private readonly BufferSlice _segment;

        private static readonly byte[] arrayByteEmpty = new byte[0];

        /// <summary>
        /// Position of this node inside a IndexPage (not persist)
        /// </summary>
        public PageAddress Position { get; }

        /// <summary>
        /// Index slot reference in CollectionIndex [1 byte]
        /// </summary>
        public byte Slot { get; }

        /// <summary>
        /// Skip-list levels (array-size) (1-32) - [1 byte]
        /// </summary>
        public byte Levels { get; }

        /// <summary>
        /// The object value that was indexed (max 255 bytes value)
        /// </summary>
        public BsonValue Key { get; }

        /// <summary>
        /// Reference for a datablock address
        /// </summary>
        public PageAddress DataBlock { get; }

        /// <summary>
        /// Single linked-list for all nodes from a single document [5 bytes]
        /// </summary>
        public PageAddress NextNode { get; private set; }

        /// <summary>
        /// Link to prev value (used in skip lists - Prev.Length = Next.Length) [5 bytes]
        /// </summary>
        public PageAddress[] Prev { get; private set; }

        /// <summary>
        /// Link to next value (used in skip lists - Prev.Length = Next.Length)
        /// </summary>
        public PageAddress[] Next { get; private set; }

        /// <summary>
        /// Get index page reference
        /// </summary>
        public IndexPage Page => _page;

        /// <summary>
        /// Calculate how many bytes this node will need on page segment
        /// </summary>
        public static int GetNodeLength(byte level, BsonValue key, out int keyLength)
        {
            keyLength = GetKeyLength(key, true);

            return INDEX_NODE_FIXED_SIZE +
                (level * 2 * PageAddress.SIZE) + // prev/next
                keyLength; // key
        }

        /// <summary>
        /// Get how many bytes will be used to store this value. Must consider:
        /// [1 byte] - BsonType
        /// [1 byte] - KeyLength (used only in String|Byte[])
        /// [N bytes] - BsonValue in bytes (0-254)
        /// </summary>
        public static int GetKeyLength(BsonValue key, bool recalc)
        {
            return 1 +
                ((key.IsString || key.IsBinary) ? 1 : 0) +
                key.GetBytesCount(recalc);
        }

        /// <summary>
        /// Read index node from page segment (lazy-load)
        /// </summary>
        public IndexNode(IndexPage page, byte index, BufferSlice segment)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.Slot = segment.ReadByte(P_SLOT);
            this.Levels = segment.ReadByte(P_LEVELS);
            this.DataBlock = segment.ReadPageAddress(P_DATA_BLOCK);
            this.NextNode = segment.ReadPageAddress(P_NEXT_NODE);

            this.Next = new PageAddress[this.Levels];
            this.Prev = new PageAddress[this.Levels];

            for (var i = 0; i < this.Levels; i++)
            {
                this.Prev[i] = segment.ReadPageAddress(P_PREV_NEXT + (i * PageAddress.SIZE * 2));
                this.Next[i] = segment.ReadPageAddress(P_PREV_NEXT + (i * PageAddress.SIZE * 2) + PageAddress.SIZE);
            }

            this.Key = segment.ReadIndexKey(P_KEY);
        }

        /// <summary>
        /// Create new index node and persist into page segment
        /// </summary>
        public IndexNode(IndexPage page, byte index, BufferSlice segment, byte slot, byte levels, BsonValue key, PageAddress dataBlock)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.Slot = slot;
            this.Levels = levels;
            this.DataBlock = dataBlock;
            this.NextNode = PageAddress.Empty;
            this.Next = new PageAddress[levels];
            this.Prev = new PageAddress[levels];
            this.Key = key;

            // persist in buffer read only data
            segment.Write(slot, P_SLOT);
            segment.Write(levels, P_LEVELS);
            segment.Write(dataBlock, P_DATA_BLOCK);
            segment.Write(this.NextNode, P_NEXT_NODE);

            for (var i = 0; i < levels; i++)
            {
                this.SetPrev((byte)i, PageAddress.Empty);
                this.SetNext((byte)i, PageAddress.Empty);
            }

            segment.WriteIndexKey(key, P_KEY);

            page.IsDirty = true;
        }

        /// <summary>
        /// Create a fake index node used only in Virtual Index runner
        /// </summary>
        public IndexNode(BsonDocument doc)
        {
            _page = null;
            _segment = new BufferSlice(arrayByteEmpty, 0, 0);

            this.Position = new PageAddress(0, 0);
            this.Slot = 0;
            this.Levels = 0;
            this.DataBlock = PageAddress.Empty;
            this.NextNode = PageAddress.Empty;
            this.Next = new PageAddress[0];
            this.Prev = new PageAddress[0];

            // index node key IS document
            this.Key = doc;
        }

        /// <summary>
        /// Update NextNode pointer (update in buffer too). Also, set page as dirty
        /// </summary>
        public void SetNextNode(PageAddress value)
        {
            this.NextNode = value;

            _segment.Write(value, P_NEXT_NODE);

            _page.IsDirty = true;
        }

        /// <summary>
        /// Update Prev[index] pointer (update in buffer too). Also, set page as dirty
        /// </summary>
        public void SetPrev(byte level, PageAddress value)
        {
            ENSURE(level <= this.Levels, "out of index in level");

            this.Prev[level] = value;

            _segment.Write(value, P_PREV_NEXT + (level * PageAddress.SIZE * 2));

            _page.IsDirty = true;
        }

        /// <summary>
        /// Update Next[index] pointer (update in buffer too). Also, set page as dirty
        /// </summary>
        public void SetNext(byte level, PageAddress value)
        {
            ENSURE(level <= this.Levels, "out of index in level");

            this.Next[level] = value;

            _segment.Write(value, P_PREV_NEXT + (level * PageAddress.SIZE * 2) + PageAddress.SIZE);

            _page.IsDirty = true;
        }

        /// <summary>
        /// Returns Next (order == 1) OR Prev (order == -1)
        /// </summary>
        public PageAddress GetNextPrev(byte level, int order)
        {
            return order == Query.Ascending ? this.Next[level] : this.Prev[level];
        }

        public override string ToString()
        {
            return $"Pos: [{this.Position}] - Key: {this.Key}";
        }
    }
}