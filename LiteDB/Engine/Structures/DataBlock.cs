using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class DataBlock
    {
        /// <summary>
        /// Get fixed part of DataBlock (6 bytes)
        /// </summary>
        public const int DATA_BLOCK_FIXED_SIZE = 1 + // Extend
                                                 PageAddress.SIZE; // NextBlock

        public const int P_EXTEND = 0; // 00-00 [byte]
        public const int P_NEXT_BLOCK = 1; // 01-05 [pageAddress]
        public const int P_BUFFER = 6; // 06-EOF [byte[]]

        private readonly DataPage _page;
        private readonly BufferSlice _segment;

        /// <summary>
        /// Position block inside page
        /// </summary>
        public PageAddress Position { get; }

        /// <summary>
        /// Indicate if this data block is first block (false) or extend block (true)
        /// </summary>
        public bool Extend { get; }

        /// <summary>
        /// If document need more than 1 block, use this link to next block
        /// </summary>
        public PageAddress NextBlock { get; private set; }

        /// <summary>
        /// Document buffer slice
        /// </summary>
        public BufferSlice Buffer { get; }

        /// <summary>
        /// Read new DataBlock from filled page segment
        /// </summary>
        public DataBlock(DataPage page, byte index, BufferSlice segment)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);

            // byte 00: Extend
            this.Extend = segment.ReadBool(P_EXTEND);

            // byte 01-05: NextBlock (PageID, Index)
            this.NextBlock = segment.ReadPageAddress(P_NEXT_BLOCK);

            // byte 06-EOL: Buffer
            this.Buffer = segment.Slice(P_BUFFER, segment.Count - P_BUFFER);
        }

        /// <summary>
        /// Create new DataBlock and fill into buffer
        /// </summary>
        public DataBlock(DataPage page, byte index, BufferSlice segment, bool extend, PageAddress nextBlock)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);

            this.NextBlock = nextBlock;
            this.Extend = extend;

            // byte 00: Data Index
            segment.Write(extend, P_EXTEND);

            // byte 01-05 (can be updated in "UpdateNextBlock")
            segment.Write(nextBlock, P_NEXT_BLOCK);

            // byte 06-EOL: Buffer
            this.Buffer = segment.Slice(P_BUFFER, segment.Count - P_BUFFER);

            page.IsDirty = true;
        }

        public void SetNextBlock(PageAddress nextBlock)
        {
            this.NextBlock = nextBlock;

            // update segment buffer with NextBlock (uint + byte)
            _segment.Write(nextBlock, P_NEXT_BLOCK);

            _page.IsDirty = true;
        }

        public override string ToString()
        {
            return $"Pos: [{this.Position}] - Ext: [{this.Extend}] - Next: [{this.NextBlock}] - Buffer: [{this.Buffer}]";
        }
    }
}