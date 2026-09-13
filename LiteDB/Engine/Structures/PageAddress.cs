using System;
using System.Collections.Generic;
using System.Diagnostics;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represents a page address inside a page structure - index could be byte offset position OR index in a list (6 bytes)
    /// </summary>
    [DebuggerStepThrough]
    internal struct PageAddress
    {
        public const int SIZE = 5;

        public static PageAddress Empty = new PageAddress(uint.MaxValue, byte.MaxValue);

        /// <summary>
        /// PageID (4 bytes)
        /// </summary>
        public readonly uint PageID;

        /// <summary>
        /// Page Segment index inside page (1 bytes)
        /// </summary>
        public readonly byte Index;

        /// <summary>
        /// Returns true if this PageAdress is empty value
        /// </summary>
        public bool IsEmpty => this.PageID == uint.MaxValue && this.Index == byte.MaxValue;

        public override bool Equals(object obj)
        {
            var other = (PageAddress)obj;

            return this.PageID == other.PageID && this.Index == other.Index;
        }

        public static bool operator ==(PageAddress lhs, PageAddress rhs)
        {
            return lhs.PageID == rhs.PageID && lhs.Index == rhs.Index;
        }

        public static bool operator !=(PageAddress lhs, PageAddress rhs)
        {
            return !(lhs == rhs);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (int)this.PageID;
                hash = hash * 23 + this.Index;
                return hash;
            }
        }

        public PageAddress(uint pageID, byte index)
        {
            this.PageID = pageID;
            this.Index = index;
        }

        public override string ToString()
        {
            return this.IsEmpty ? "(empty)" : this.PageID.ToString().PadLeft(4, '0') + ":" + this.Index.ToString().PadLeft(2, '0');
        }

        public BsonValue ToBsonValue()
        {
            if (this.IsEmpty) return BsonValue.Null;

            return new BsonDocument
            {
                ["pageID"] = (int)this.PageID,
                ["index"] = (int)this.Index
            };
        }
    }
}