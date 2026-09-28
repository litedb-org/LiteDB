using System;

namespace LiteDB.Engine
{
    /// <summary>Preserves the known damaged structure in the opening recovery report.</summary>
    internal sealed class LegacyFileException : LiteException
    {
        internal PageType PageType { get; }
        internal PageAddress Address { get; }
        internal string Collection { get; }

        internal LegacyFileException(PageType pageType, PageAddress address, string collection,
            Exception inner, string message, params object[] args)
            : base(INVALID_DATAFILE_STATE, inner, message, args)
        {
            this.PageType = pageType;
            this.Address = address;
            this.Collection = collection;
        }
    }
}
