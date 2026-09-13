using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represents a page position after save in disk. Used in WAL files where PageID do not match with PagePosition
    /// </summary>
    internal struct PagePosition
    {
        public static PagePosition Empty => new PagePosition(uint.MaxValue, long.MaxValue);

        /// <summary>
        /// PageID (4 bytes)
        /// </summary>
        public uint PageID;

        /// <summary>
        /// Position in disk
        /// </summary>
        public long Position;

        /// <summary>
        /// Checks if current PagePosition is empty value
        /// </summary>
        public bool IsEmpty => this.PageID == uint.MaxValue && this.Position == long.MaxValue;

        public override bool Equals(object obj)
        {
            var other = (PagePosition)obj;

            return this.PageID == other.PageID && this.Position == other.PageID;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (int)this.PageID;
                hash = hash * 23 + (int)this.Position;
                return hash;
            }
        }

        public PagePosition(uint pageID, long position)
        {
            this.PageID = pageID;
            this.Position = position;
        }

        public override string ToString()
        {
            return this.IsEmpty ? "----:----" :
                (this.PageID == uint.MaxValue ? "----" : this.PageID.ToString()) + ":" + this.Position.ToString();
        }
    }
}