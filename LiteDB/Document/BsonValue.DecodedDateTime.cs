using System;

namespace LiteDB
{
    public partial class BsonValue
    {
        internal static BsonValue FromDecodedDateTime(DateTime value)
        {
            // The stored value already has BSON millisecond precision. Rebuilding
            // its components would lose .NET's hidden ambiguous-DST flag and could
            // break grouping order after an index key is projected into local time.
            return new BsonValue(BsonType.DateTime, value);
        }
    }
}
