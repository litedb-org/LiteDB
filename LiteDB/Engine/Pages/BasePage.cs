using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal enum PageType { Empty = 0, Header = 1, Collection = 2, Index = 3, Data = 4 }

    internal class BasePage
    {
        protected readonly PageBuffer _buffer;

        /// <summary>
        /// Bytes used in each offset slot (to store segment position (2) + length (2))
        /// </summary>
        public const int SLOT_SIZE = 4;

        #region Buffer Field Positions

        public const int P_PAGE_ID = 0;  // 00-03 [uint]
        public const int P_PAGE_TYPE = 4; // 04-04 [byte]
        public const int P_PREV_PAGE_ID = 5; // 05-08 [uint]
        public const int P_NEXT_PAGE_ID = 9; // 09-12 [uint]
        public const int P_INITIAL_SLOT = 13; // 13-13 [byte]

        public const int P_TRANSACTION_ID = 14; // 14-17 [uint]
        public const int P_IS_CONFIRMED = 18; // 18-18 [byte]
        public const int P_COL_ID = 19; // 19-22 [uint]

        public const int P_ITEMS_COUNT = 23; // 23-23 [byte]
        public const int P_USED_BYTES = 24; // 24-25 [ushort]
        public const int P_FRAGMENTED_BYTES = 26; // 26-27 [ushort]
        public const int P_NEXT_FREE_POSITION = 28; // 28-29 [ushort]
        public const int P_HIGHEST_INDEX = 30; // 30-30 [byte]

        #endregion

        /// <summary>
        /// Represent page number - start in 0 with HeaderPage [4 bytes]
        /// </summary>
        public uint PageID { get; }

        /// <summary>
        /// Indicate the page type [1 byte]
        /// </summary>
        public PageType PageType { get; private set; }

        /// <summary>
        /// Represent the previous page. Used for page-sequences - MaxValue represent that has NO previous page [4 bytes]
        /// </summary>
        public uint PrevPageID { get; set; }

        /// <summary>
        /// Represent the next page. Used for page-sequences - MaxValue represent that has NO next page [4 bytes]
        /// </summary>
        public uint NextPageID { get; set; }

        /// <summary>
        /// Get/Set where this page are in free list slot [1 byte]
        /// Used only in DataPage (0-4) and IndexPage (0-1) - when new or not used: 255
        /// DataPage: 0 (7344 - 8160 free space) - 1 (6120 - 7343) - 2 (4896 - 6119) - 3 (2448 - 4895) - 4 (0 - 2447)
        /// IndexPage 0 (1400 - 8160 free bytes) - 1 (0 - 1399 bytes free)
        /// </summary>
        public byte PageListSlot { get; set; }

        /// <summary>
        /// Indicate how many items are used inside this page [1 byte]
        /// </summary>
        public byte ItemsCount { get; private set; }

        /// <summary>
        /// Get how many bytes are used on content area (exclude header and footer blocks) [2 bytes]
        /// </summary>
        public ushort UsedBytes { get; private set; }

        /// <summary>
        /// Get how many bytes are fragmented inside this page (free blocks inside used blocks) [2 bytes]
        /// </summary>
        public ushort FragmentedBytes { get; private set; }

        /// <summary>
        /// Get next free position. Starts with 32 (first byte after header) - There is no fragmentation after this [2 bytes]
        /// </summary>
        public ushort NextFreePosition { get; private set; }

        /// <summary>
        /// Get last (highest) used index slot - use byte.MaxValue for empty [1 byte]
        /// </summary>
        public byte HighestIndex { get; private set; }

        /// <summary>
        /// Get how many free bytes (including fragmented bytes) are in this page (content space) - Will return 0 bytes if page are full (or with max 255 items)
        /// </summary>
        public int FreeBytes => this.ItemsCount == byte.MaxValue ?
            0 :
            PAGE_SIZE - PAGE_HEADER_SIZE - this.UsedBytes - this.FooterSize;

        /// <summary>
        /// Get how many bytes are used in footer page at this moment
        /// ((HighestIndex + 1) * 4 bytes per slot: [2 for position, 2 for length])
        /// </summary>
        public int FooterSize =>
            (this.HighestIndex == byte.MaxValue ?
            0 :  // no items in page
            ((this.HighestIndex + 1) * SLOT_SIZE)); // 4 bytes PER item (2 to position + 2 to length) - need consider HighestIndex used

        /// <summary>
        /// Set in all datafile pages the page id about data/index collection. Useful if want re-build database without any index [4 bytes]
        /// </summary>
        public uint ColID { get; set; }

        /// <summary>
        /// Represent transaction ID that was stored [4 bytes]
        /// </summary>
        public uint TransactionID { get; set; }

        /// <summary>
        /// Used in WAL, define this page is last transaction page and are confirmed on disk [1 byte]
        /// </summary>
        public bool IsConfirmed { get; set; }

        /// <summary>
        /// Set this pages that was changed and must be persist in disk [not peristable]
        /// </summary>
        public bool IsDirty { get; set; }

        /// <summary>
        /// Get page buffer instance
        /// </summary>
        public PageBuffer Buffer => _buffer;

        #region Initialize/Update buffer

        /// <summary>
        /// Create new Page based on pre-defined PageID and PageType
        /// </summary>
        public BasePage(PageBuffer buffer, uint pageID, PageType pageType)
        {
            _buffer = buffer;

            DEBUG(buffer.Slice(PAGE_HEADER_SIZE, PAGE_SIZE - PAGE_HEADER_SIZE - 1).All(0), "new page buffer must be empty before use in a new page");

            // page information
            this.PageID = pageID;
            this.PageType = pageType;
            this.PrevPageID = uint.MaxValue;
            this.NextPageID = uint.MaxValue;
            this.PageListSlot = byte.MaxValue; // no slot

            // transaction information
            this.ColID = uint.MaxValue;
            this.TransactionID = uint.MaxValue;
            this.IsConfirmed = false;

            // block information
            this.ItemsCount = 0;
            this.UsedBytes = 0;
            this.FragmentedBytes = 0;
            this.NextFreePosition = PAGE_HEADER_SIZE; // 32
            this.HighestIndex = byte.MaxValue; // empty - not used yet

            // default values
            this.IsDirty = false;

            // writing direct into buffer in Ctor() because there is no change later (write once)
            _buffer.Write(this.PageID, P_PAGE_ID);
            _buffer.Write((byte)this.PageType, P_PAGE_TYPE);
        }

        /// <summary>
        /// Read header data from byte[] buffer into local variables
        /// using fixed position be be faster than use BufferReader
        /// </summary>
        public BasePage(PageBuffer buffer)
        {
            _buffer = buffer;

            // page information
            this.PageID = _buffer.ReadUInt32(P_PAGE_ID);
            this.PageType = (PageType)_buffer.ReadByte(P_PAGE_TYPE);
            this.PrevPageID = _buffer.ReadUInt32(P_PREV_PAGE_ID);
            this.NextPageID = _buffer.ReadUInt32(P_NEXT_PAGE_ID);
            this.PageListSlot = _buffer.ReadByte(P_INITIAL_SLOT);

            // transaction information
            this.TransactionID = _buffer.ReadUInt32(P_TRANSACTION_ID);
            this.IsConfirmed = _buffer.ReadBool(P_IS_CONFIRMED);
            this.ColID = _buffer.ReadUInt32(P_COL_ID);

            // blocks information
            this.ItemsCount = _buffer.ReadByte(P_ITEMS_COUNT);
            this.UsedBytes = _buffer.ReadUInt16(P_USED_BYTES);
            this.FragmentedBytes = _buffer.ReadUInt16(P_FRAGMENTED_BYTES);
            this.NextFreePosition = _buffer.ReadUInt16(P_NEXT_FREE_POSITION);
            this.HighestIndex = _buffer.ReadByte(P_HIGHEST_INDEX);
        }

        /// <summary>
        /// Write header data from variable into byte[] buffer. When override, call base.UpdateBuffer() after write your code
        /// </summary>
        public virtual PageBuffer UpdateBuffer()
        {
            // using fixed position to be faster than BufferWriter
            ENSURE(this.PageID == _buffer.ReadUInt32(P_PAGE_ID), "pageID can't be changed");

            // page information
            // PageID - never change!
            _buffer.Write(this.PrevPageID, P_PREV_PAGE_ID);
            _buffer.Write(this.NextPageID, P_NEXT_PAGE_ID);
            _buffer.Write(this.PageListSlot, P_INITIAL_SLOT);

            // transaction information
            _buffer.Write(this.TransactionID, P_TRANSACTION_ID);
            _buffer.Write(this.IsConfirmed, P_IS_CONFIRMED);
            _buffer.Write(this.ColID, P_COL_ID);

            // segments information
            _buffer.Write(this.ItemsCount, P_ITEMS_COUNT);
            _buffer.Write(this.UsedBytes, P_USED_BYTES);
            _buffer.Write(this.FragmentedBytes, P_FRAGMENTED_BYTES);
            _buffer.Write(this.NextFreePosition, P_NEXT_FREE_POSITION);
            _buffer.Write(this.HighestIndex, P_HIGHEST_INDEX);

            return _buffer;
        }

        /// <summary>
        /// Change current page to Empty page - fix variables and buffer (DO NOT change PageID)
        /// </summary>
        public void MarkAsEmtpy()
        {
            this.IsDirty = true;

            // page information
            // PageID never change
            this.PageType = PageType.Empty;
            this.PrevPageID = uint.MaxValue;
            this.NextPageID = uint.MaxValue;
            this.PageListSlot = byte.MaxValue;

            // transaction information
            this.ColID = uint.MaxValue;
            this.TransactionID = uint.MaxValue;
            this.IsConfirmed = false;

            // block information
            this.ItemsCount = 0;
            this.UsedBytes = 0;
            this.FragmentedBytes = 0;
            this.NextFreePosition = PAGE_HEADER_SIZE; // 32
            this.HighestIndex = byte.MaxValue; // empty - not used yet

            // MUST CLEAR CONTENT
            // because this page will be readed when re-used
            _buffer.Clear(PAGE_HEADER_SIZE, PAGE_SIZE - PAGE_HEADER_SIZE);

            // fix buffer page type position
            _buffer.Write((byte)this.PageType, P_PAGE_TYPE);
        }

        #endregion

        #region Access/Manipulate PageSegments

        /// <summary>
        /// Get a page segment item based on index slot
        /// </summary>
        public BufferSlice Get(byte index)
        {
            ENSURE(this.ItemsCount > 0, "should have items in this page");
            ENSURE(this.HighestIndex != byte.MaxValue, "should have at least 1 index in this page");
            ENSURE(index <= this.HighestIndex, "get only index below highest index");

            // read slot address
            var positionAddr = CalcPositionAddr(index);
            var lengthAddr = CalcLengthAddr(index);

            // read segment position/length
            var position = _buffer.ReadUInt16(positionAddr);
            var length = _buffer.ReadUInt16(lengthAddr);

            ENSURE(this.IsValidPos(position), "invalid segment position in index footer: {0}/{1}", this, index);
            ENSURE(this.IsValidLen(length), "invalid segment length in index footer: {0}/{1}", this, index);

            // return buffer slice with content only data
            return _buffer.Slice(position, length);
        }

        /// <summary>
        /// Get a new page segment for this length content
        /// </summary>
        public BufferSlice Insert(ushort bytesLength, out byte index)
        {
            index = byte.MaxValue;

            return this.InternalInsert(bytesLength, ref index);
        }

        /// <summary>
        /// Get a new page segment for this length content using fixed index
        /// </summary>
        private BufferSlice InternalInsert(ushort bytesLength, ref byte index)
        {
            var isNewInsert = index == byte.MaxValue;

            ENSURE(_buffer.ShareCounter == BUFFER_WRITABLE, "page must be writable to support changes");
            ENSURE(bytesLength > 0, "must insert more than 0 bytes");
            ENSURE(this.FreeBytes >= bytesLength + (isNewInsert ? SLOT_SIZE : 0), "length must be always lower than current free space");
            ENSURE(this.ItemsCount < byte.MaxValue, "page full");
            ENSURE(this.FreeBytes >= this.FragmentedBytes, "fragmented bytes must be at most free bytes");

            if(!(this.FreeBytes >= bytesLength + (isNewInsert ? SLOT_SIZE : 0)))
            {
                throw LiteException.InvalidFreeSpacePage(this.PageID, this.FreeBytes, bytesLength + (isNewInsert ? SLOT_SIZE : 0));
            }

            // calculate how many continuous bytes are avaiable in this page
            var continuousBlocks = this.FreeBytes - this.FragmentedBytes - (isNewInsert ? SLOT_SIZE : 0);

            ENSURE(continuousBlocks == PAGE_SIZE - this.NextFreePosition - this.FooterSize - (isNewInsert ? SLOT_SIZE : 0), "continuousBlock must be same as from NextFreePosition");

            // if continuous blocks are not enough for this data, must run page defrag
            if (bytesLength > continuousBlocks)
            {
                this.Defrag();
            }

            // if index is new insert segment, must request for new Index
            if (index == byte.MaxValue)
            {
                // get new free index must run after defrag
                index = this.GetFreeIndex();
            }

            if (index > this.HighestIndex || this.HighestIndex == byte.MaxValue)
            {
                ENSURE(index == (byte)(this.HighestIndex + 1), "new index must be next highest index");

                this.HighestIndex = index;
            }

            // get segment addresses
            var positionAddr = CalcPositionAddr(index);
            var lengthAddr = CalcLengthAddr(index);

            ENSURE(_buffer.ReadUInt16(positionAddr) == 0, "slot position must be empty before use");
            ENSURE(_buffer.ReadUInt16(lengthAddr) == 0, "slot length must be empty before use");

            // get next free position in page
            var position = this.NextFreePosition;

            // write this page position in my position address
            _buffer.Write(position, positionAddr);

            // write page segment length in my length address
            _buffer.Write(bytesLength, lengthAddr);

            // update next free position and counters
            this.ItemsCount++;
            this.UsedBytes += bytesLength;
            this.NextFreePosition += bytesLength;

            this.IsDirty = true;

            ENSURE(position + bytesLength <= (PAGE_SIZE - (this.HighestIndex + 1) * SLOT_SIZE), "new buffer slice could not override footer area");

            // create page segment based new inserted segment
            return _buffer.Slice(position, bytesLength);
        }

        /// <summary>
        /// Remove index slot about this page segment
        /// </summary>
        public void Delete(byte index)
        {
            ENSURE(_buffer.ShareCounter == BUFFER_WRITABLE, "page must be writable to support changes");

            // read block position on index slot
            var positionAddr = CalcPositionAddr(index);
            var lengthAddr = CalcLengthAddr(index);

            var position = _buffer.ReadUInt16(positionAddr);
            var length = _buffer.ReadUInt16(lengthAddr);

            ENSURE(this.IsValidPos(position), "invalid segment position");
            ENSURE(this.IsValidLen(length), "invalid segment length");

            // clear both position/length
            _buffer.Write((ushort)0, positionAddr);
            _buffer.Write((ushort)0, lengthAddr);

            // add as free blocks
            this.ItemsCount--;
            this.UsedBytes -= length;

            // clean segment area with \0 [[can be removed later for production]]
            _buffer.Array.Fill(0, _buffer.Offset + position, length);

            // check if deleted segment are at end of page
            var isLastSegment = (position + length == this.NextFreePosition);

            if (isLastSegment)
            {
                // update next free position with this deleted position
                this.NextFreePosition = position;
            }
            else
            {
                // if segment is in middle of the page, add this blocks as fragment block
                this.FragmentedBytes += length;
            }

            // if deleted if are HighestIndex, update HighestIndex
            if (this.HighestIndex == index)
            {
                this.UpdateHighestIndex();
            }

            // reset start index (used in GetFreeIndex)
            _startIndex = 0;

            // if there is no more items in page, fix fragmentation
            if (this.ItemsCount == 0)
            {
                ENSURE(this.HighestIndex == byte.MaxValue, "if there is no items, HighestIndex must be clear");
                ENSURE(this.UsedBytes == 0, "should be no bytes used in clean page");
                DEBUG(_buffer.Slice(PAGE_HEADER_SIZE, PAGE_SIZE - PAGE_HEADER_SIZE - 1).All(0), "all content area must be 0");

                this.NextFreePosition = PAGE_HEADER_SIZE;
                this.FragmentedBytes = 0;
            }

            // set page as dirty
            this.IsDirty = true;
        }

        /// <summary>
        /// Update segment bytes with new data. Current page must have bytes enougth for this new size. Index will not be changed
        /// Update will try use same segment to store. If not possible, write on end of page (with possible Defrag operation)
        /// </summary>
        public BufferSlice Update(byte index, ushort bytesLength)
        {
            ENSURE(_buffer.ShareCounter == BUFFER_WRITABLE, "page must be writable to support changes");
            ENSURE(bytesLength > 0, "must update more than 0 bytes");

            // read slot address
            var positionAddr = CalcPositionAddr(index);
            var lengthAddr = CalcLengthAddr(index);

            // read segment position/length
            var position = _buffer.ReadUInt16(positionAddr);
            var length = _buffer.ReadUInt16(lengthAddr);

            ENSURE(this.IsValidPos(position), "invalid segment position");
            ENSURE(this.IsValidLen(length), "invalid segment length");

            // check if deleted segment are at end of page
            var isLastSegment = (position + length == this.NextFreePosition);

            // mark page as dirty before return buffer slice
            this.IsDirty = true;

            // best situation: same slice length
            if (bytesLength == length)
            {
                return _buffer.Slice(position, length);
            }
            // when new length are less than original length (will fit in current segment)
            else if (bytesLength < length)
            {
                var diff = (ushort)(length - bytesLength); // bytes removed (should > 0)

                if (isLastSegment)
                {
                    // if is at end of page, must get back unused blocks
                    this.NextFreePosition -= diff;
                }
                else
                {
                    // is this segment are not at end, must add this as fragment
                    this.FragmentedBytes += diff;
                }

                // less blocks will be used
                this.UsedBytes -= diff;

                // update length
                _buffer.Write(bytesLength, lengthAddr);

                // clear fragment bytes
                _buffer.Clear(position + bytesLength, diff);

                return _buffer.Slice(position, bytesLength);
            }
            // when new length are large than current segment must remove current item and add again
            else
            {
                // clear current segment
                _buffer.Clear(position, length);

                this.ItemsCount--;
                this.UsedBytes -= length;

                if (isLastSegment)
                {
                    // if segment is end of page, must update next free position to current segment position
                    this.NextFreePosition = position;
                }
                else
                {
                    // if segment is on middle of page, add content length as fragment bytes
                    this.FragmentedBytes += length;
                }

                // clear slot index position/length
                _buffer.Write((ushort)0, positionAddr);
                _buffer.Write((ushort)0, lengthAddr);

                // call insert
                return this.InternalInsert(bytesLength, ref index);
            }
        }

        /// <summary>
        /// Defrag method re-organize all byte data content removing all fragmented data. This will move all page segments
        /// to create a single continuous content area (just after header area). No index segment will be changed (only positions)
        /// </summary>
        public void Defrag()
        {
            ENSURE(this.FragmentedBytes > 0, "do not call this when page has no fragmentation");
            ENSURE(_buffer.ShareCounter == BUFFER_WRITABLE, "page must be writable to support changes");
            ENSURE(this.HighestIndex < byte.MaxValue, "there is no items in this page to run defrag");

            LOG($"defrag page #{this.PageID} (fragments: {this.FragmentedBytes})", "DISK");

            // first get all segments inside this page sorted by position (position, index)
            var segments = new SortedList<ushort, byte>();

            // use int to avoid byte overflow
            for (int index = 0; index <= this.HighestIndex; index++)
            {
                var positionAddr = CalcPositionAddr((byte)index);
                var position = _buffer.ReadUInt16(positionAddr);

                // get only used index
                if (position != 0)
                {
                    ENSURE(this.IsValidPos(position), "invalid segment position");

                    // sort by position
                    segments.Add(position, (byte)index);
                }
            }

            // here first block position
            var next = (ushort)PAGE_HEADER_SIZE;

            // now, list all segments order by Position
            foreach (var slot in segments)
            {
                var index = slot.Value;
                var position = slot.Key;

                // get segment length
                var lengthAddr = CalcLengthAddr(index);
                var length = _buffer.ReadUInt16(lengthAddr);

                ENSURE(this.IsValidLen(length), "invalid segment length");

                // if current segment are not as excpect, copy buffer to right position (excluding empty space)
                if (position != next)
                {
                    ENSURE(position > next, "current segment position must be greater than current empty space");

                    // copy from original position into new (correct) position
                    System.Buffer.BlockCopy(_buffer.Array,
                        _buffer.Offset + position,
                        _buffer.Array,
                        _buffer.Offset + next,
                        length);

                    // update index slot with this new block position
                    var positionAddr = CalcPositionAddr(index);

                    _buffer.Write(next, positionAddr);
                }

                next += length;
            }

            // fill all non-used content area with 0
            var emptyLength = PAGE_SIZE - next - this.FooterSize;

            _buffer.Array.Fill(0, _buffer.Offset + next, emptyLength);

            // clear fragment blocks (page are in a continuous segment)
            this.FragmentedBytes = 0;
            this.NextFreePosition = next;
         }

        /// <summary>
        /// Store start index used in GetFreeIndex to avoid always run full loop over all indexes
        /// </summary>
        private byte _startIndex = 0;

        /// <summary>
        /// Get a free index slot in this page
        /// </summary>
        private byte GetFreeIndex()
        {
            // check for all slot area to get first empty slot [safe for byte loop]
            for (byte index = _startIndex; index <= this.HighestIndex; index++)
            {
                var positionAddr = CalcPositionAddr(index);
                var position = _buffer.ReadUInt16(positionAddr);

                // if position = 0 means this slot is not used
                if (position == 0)
                {
                    _startIndex = (byte)(index + 1);

                    return index;
                }
            }

            return (byte)(this.HighestIndex + 1);
        }

        /// <summary>
        /// Get all used slots indexes in this page
        /// </summary>
        public IEnumerable<byte> GetUsedIndexs()
        {
            // check for empty before loop
            if (this.ItemsCount == 0) yield break;

            ENSURE(this.HighestIndex != byte.MaxValue, "if has items count, Highest index should be not empty");

            // [safe for byte loop] - because this.HighestIndex can't be 255
            for (byte index = 0; index <= this.HighestIndex; index++)
            {
                var positionAddr = CalcPositionAddr(index);
                var position = _buffer.ReadUInt16(positionAddr);

                if (position != 0)
                {
                    yield return index;
                }
            }
        }

        /// <summary>
        /// Update HighestIndex based on current HighestIndex (step back looking for next used slot)
        /// Used only in Delete() operation
        /// </summary>
        private void UpdateHighestIndex()
        {
            ENSURE(this.HighestIndex < byte.MaxValue, "can run only if contains a valid HighestIndex");

            // if current index is 0, clear index
            if (this.HighestIndex == 0)
            {
                this.HighestIndex = byte.MaxValue;
                return;
            }

            // start from current - 1 to 0 (should use "int" because for use ">= 0")
            for (int index = this.HighestIndex - 1; index >= 0; index--)
            {
                var positionAddr = CalcPositionAddr((byte)index);
                var position = _buffer.ReadUInt16(positionAddr);

                if (position != 0)
                {
                    ENSURE(this.IsValidPos(position), "invalid segment position");

                    this.HighestIndex = (byte)index;
                    return;
                }
            }

            // there is no more slots used
            this.HighestIndex = byte.MaxValue;
        }

        /// <summary>
        /// Checks if segment position has a valid value (used for DEBUG)
        /// </summary>
        private bool IsValidPos(ushort position) => position >= PAGE_HEADER_SIZE && position < (PAGE_SIZE - this.FooterSize);

        /// <summary>
        /// Checks if segment length has a valid value (used for DEBUG)
        /// </summary>
        private bool IsValidLen(ushort length) => length > 0 && length <= (PAGE_SIZE - PAGE_HEADER_SIZE - this.FooterSize);

        #endregion

        #region Static Helpers

        /// <summary>
        /// Get buffer offset position where one page segment length are located (based on index slot)
        /// </summary>
        public static int CalcPositionAddr(byte index) => PAGE_SIZE - ((index + 1) * SLOT_SIZE) + 2;

        /// <summary>
        /// Get buffer offset position where one page segment length are located (based on index slot)
        /// </summary>
        public static int CalcLengthAddr(byte index) => PAGE_SIZE - ((index + 1) * SLOT_SIZE);

        /// <summary>
        /// Returns a size of specified number of pages
        /// </summary>
        public static long GetPagePosition(uint pageID)
        {
            return checked((long)pageID * PAGE_SIZE);
        }

        /// <summary>
        /// Returns a size of specified number of pages
        /// </summary>
        public static long GetPagePosition(int pageID)
        {
            ENSURE(pageID >= 0, "page could not be less than 0.");

            return BasePage.GetPagePosition((uint)pageID);
        }

        /// <summary>
        /// Create new page instance based on buffer (READ)
        /// </summary>
        public static T ReadPage<T>(PageBuffer buffer)
            where T : BasePage
        {
            if (typeof(T) == typeof(BasePage)) return (T)(object)new BasePage(buffer);
            if (typeof(T) == typeof(HeaderPage)) return (T)(object)new HeaderPage(buffer);
            if (typeof(T) == typeof(CollectionPage)) return (T)(object)new CollectionPage(buffer);
            if (typeof(T) == typeof(IndexPage)) return (T)(object)new IndexPage(buffer);
            if (typeof(T) == typeof(DataPage)) return (T)(object)new DataPage(buffer);

            throw new InvalidCastException();
        }

        /// <summary>
        /// Create new page instance with new PageID and passed buffer (NEW)
        /// </summary>
        public static T CreatePage<T>(PageBuffer buffer, uint pageID)
            where T : BasePage
        {
            if (typeof(T) == typeof(CollectionPage)) return (T)(object)new CollectionPage(buffer, pageID);
            if (typeof(T) == typeof(IndexPage)) return (T)(object)new IndexPage(buffer, pageID);
            if (typeof(T) == typeof(DataPage)) return (T)(object)new DataPage(buffer, pageID);

            throw new InvalidCastException();
        }

        #endregion

        public override string ToString()
        {
            return $"PageID: {this.PageID.ToString().PadLeft(4, '0')} : {this.PageType} ({this.ItemsCount} Items)";
        }
    }
}