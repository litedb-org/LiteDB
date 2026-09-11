using System;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal enum FrameState
    {
        Free,
        Loading,
        Readable,
        Writable
    }

    /// <summary>
    /// Represent page buffer to be read/write using FileMemory
    /// </summary>
    internal class PageBuffer : BufferSlice
    {
        /// <summary>
        /// Get, on initialize, a unique ID in all database instance for this PageBufer. Is a simple global incremented counter
        /// </summary>
        public readonly int UniqueID;

        /// <summary>
        /// Get/Set page position. If page are writable, this postion CAN be MaxValue (has not defined position yet)
        /// </summary>
        public long Position;

        /// <summary>
        /// Get/Set page bytes origin (data/log)
        /// </summary>
        public FileOrigin Origin;

        /// <summary>
        /// Get/Set how many read-share threads are using this page. -1 means 1 thread are using as writable
        /// </summary>
        public int ShareCounter;

        /// <summary>
        /// Legacy access timestamp. Kept for internal compatibility; CLOCK uses
        /// <see cref="Referenced"/> instead.
        /// </summary>
        public long Timestamp;

        internal FrameState State;
        internal long Generation;
        internal int Referenced;
        internal MemoryCacheSegment Segment;
        internal int NextFree;
        internal MemoryCache Cache;

        public PageBuffer(byte[] buffer, int offset, int uniqueID)
            : base(buffer, offset, PAGE_SIZE)
        {
            this.UniqueID = uniqueID;
            this.Position = long.MaxValue;
            this.Origin = FileOrigin.None;
            this.ShareCounter = 0;
            this.Timestamp = 0;
            this.State = FrameState.Free;
            this.Generation = 0;
            this.Referenced = 0;
            this.NextFree = -1;
            this.AttachOwner(this);
        }

        /// <summary>
        /// Release this page - decrement ShareCounter
        /// </summary>
        public void Release()
        {
            var cache = this.Cache;

            if (cache == null)
            {
                ENSURE(this.ShareCounter > 0, "share counter must be > 0 in Release()");
                Interlocked.Decrement(ref this.ShareCounter);
            }
            else
            {
                cache.Release(this);
            }
        }

#if DEBUG || TESTING
        ~PageBuffer()
        {
            ENSURE(this.ShareCounter == 0, $"share count must be 0 in destroy PageBuffer (current: {this.ShareCounter})");
        }
#endif

        public override string ToString()
        {
            var p = this.Position == long.MaxValue ? "<empty>" : this.Position.ToString();
            var s = this.ShareCounter == BUFFER_WRITABLE ? "<writable>" : this.ShareCounter.ToString();
            var pageID = this.ReadUInt32(0);
            var pageType = this[4];

            return $"ID: {this.UniqueID} - Position: {p}/{this.Origin} - Shared: {s} - ({base.ToString()}) :: Content: [{pageID.ToString("0:0000")}/{(PageType)pageType}]";
        }

        public unsafe bool IsBlank()
        {
            this.EnsureReadable();
            fixed (byte* arrayPtr = this.Array)
            {
                ulong* ptr = (ulong*)(arrayPtr + this.Offset);

                return *ptr == 0UL && *(ptr + 1) == 0UL;
            }
        }
    }
}
