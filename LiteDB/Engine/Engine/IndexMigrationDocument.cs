using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private static BsonDocument ReadMigrationDocument(BufferReader reader, string collection, PageAddress address)
        {
            var result = reader.ReadDocument();
            // Malformed BSON is corruption. Preserve I/O/device exceptions as-is:
            // a failed read is not permission to salvage and discard a record.
            if (result.Exception is NotSupportedException || result.Exception is ArgumentException ||
                result.Exception is FormatException || result.Exception is OverflowException ||
                result.Exception is LiteException lex && lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                throw new LegacyFileException(PageType.Data, address, collection, result.Exception,
                    "Damaged document in collection '{0}' at {1} during index validation: {2}",
                    collection, address, result.Exception.Message);
            return result.GetValue();
        }
    }
}
