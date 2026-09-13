using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// The DataPage thats stores object data.
    /// </summary>
    internal class DataPage : BasePage
    {
        /// <summary>
        /// Read existing DataPage in buffer
        /// </summary>
        public DataPage(PageBuffer buffer)
            : base(buffer)
        {
            ENSURE(this.PageType == PageType.Data, "Page type must be data page: {0}", PageType);

            if (this.PageType != PageType.Data) throw LiteException.InvalidPageType(PageType.Data, this);
        }

        /// <summary>
        /// Create new DataPage
        /// </summary>
        public DataPage(PageBuffer buffer, uint pageID)
            : base(buffer, pageID, PageType.Data)
        {
        }

        /// <summary>
        /// Get single DataBlock
        /// </summary>
        public DataBlock GetBlock(byte index)
        {
            var segment = base.Get(index);

            return new DataBlock(this, index, segment);
        }

        /// <summary>
        /// Insert new DataBlock. Use extend to indicate document sequence (document are large than PAGE_SIZE)
        /// </summary>
        public DataBlock InsertBlock(int bytesLength, bool extend)
        {
            var segment = base.Insert((ushort)(bytesLength + DataBlock.DATA_BLOCK_FIXED_SIZE), out var index);

            return new DataBlock(this, index, segment, extend, PageAddress.Empty);
        }

        /// <summary>
        /// Update current block returning data block to be fill
        /// </summary>
        public DataBlock UpdateBlock(DataBlock currentBlock, int bytesLength)
        {
            var segment = base.Update(currentBlock.Position.Index, (ushort)(bytesLength + DataBlock.DATA_BLOCK_FIXED_SIZE));

            return new DataBlock(this, currentBlock.Position.Index, segment, currentBlock.Extend, currentBlock.NextBlock);
        }

        /// <summary>
        /// Delete single data block inside this page
        /// </summary>
        public void DeleteBlock(byte index)
        {
            base.Delete(index);
        }

        /// <summary>
        /// Get all block positions inside this page that are not extend blocks (initial data block)
        /// </summary>
        public IEnumerable<PageAddress> GetBlocks()
        {
            foreach(var index in base.GetUsedIndexs())
            {
                var slotPosition = BasePage.CalcPositionAddr(index);
                var position = _buffer.ReadUInt16(slotPosition);

                var extend = _buffer.ReadBool(position + DataBlock.P_EXTEND);

                if (extend == false)
                {
                    yield return new PageAddress(this.PageID, index);
                }
            }
        }

        /// <summary>
        /// FreeBytes ranges on page slot for free list page
        /// 90% - 100% = 0 (7344 - 8160)
        /// 75% -  90% = 1 (6120 - 7343)
        /// 60% -  75% = 2 (4896 - 6119)
        /// 30% -  60% = 3 (2448 - 4895)
        ///  0% -  30% = 4 (0000 - 2447)
        /// </summary>
        private static readonly int[] _freePageSlots = new[]
        {
            (int)((PAGE_SIZE - PAGE_HEADER_SIZE) * .90), // 0
            (int)((PAGE_SIZE - PAGE_HEADER_SIZE) * .75), // 1
            (int)((PAGE_SIZE - PAGE_HEADER_SIZE) * .60), // 2
            (int)((PAGE_SIZE - PAGE_HEADER_SIZE) * .30)  // 3
        };

        /// <summary>
        /// Returns the slot the page should be in, given the <paramref name="freeBytes"/> it has
        /// </summary>
        /// <returns>A slot number between 0 and 4</returns>
        public static byte FreeIndexSlot(int freeBytes)
        {
            ENSURE(freeBytes >= 0, "FreeBytes must be positive: {0}", freeBytes);

            for (var i = 0; i < _freePageSlots.Length; i++)
            {
                if (freeBytes >= _freePageSlots[i]) return (byte)i;
            }

            return PAGE_FREE_LIST_SLOTS - 1; // Slot 4 (last slot)
        }

        /// <summary>
        /// Returns the slot where there is a page with enough space for <paramref name="length"/> bytes of data.
        /// Returns -1 if no space guaranteed (more than 90% of a DataPage net size)
        /// </summary>
        /// <returns>A slot number between -1 and 3</returns>
        public static int GetMinimumIndexSlot(int length)
        {
            return FreeIndexSlot(length) - 1;
        }
    }
}