namespace LiteDB
{
    /// <summary>
    /// Provides the mapper settings explicitly supported by a generated execution map.
    /// </summary>
    public readonly struct GeneratedExecutionOptions
    {
        /// <summary>
        /// Initializes generated execution settings for one operation.
        /// </summary>
        /// <param name="serializeNullValues">Whether generated serialization must persist null non-ID values.</param>
        /// <param name="trimWhitespace">Whether generated string serialization trims whitespace.</param>
        /// <param name="emptyStringToNull">Whether generated string serialization converts empty strings to BSON Null.</param>
        /// <param name="enumAsInteger">Whether generated enum serialization writes integer values.</param>
        public GeneratedExecutionOptions(
            bool serializeNullValues,
            bool trimWhitespace,
            bool emptyStringToNull,
            bool enumAsInteger)
        {
            SerializeNullValues = serializeNullValues;
            TrimWhitespace = trimWhitespace;
            EmptyStringToNull = emptyStringToNull;
            EnumAsInteger = enumAsInteger;
        }

        /// <summary>
        /// Gets whether generated serialization must persist null non-ID values.
        /// </summary>
        public bool SerializeNullValues { get; }

        /// <summary>
        /// Gets whether generated string serialization trims whitespace.
        /// </summary>
        public bool TrimWhitespace { get; }

        /// <summary>
        /// Gets whether generated string serialization converts empty strings to BSON Null.
        /// </summary>
        public bool EmptyStringToNull { get; }

        /// <summary>
        /// Gets whether generated enum serialization writes integer values.
        /// </summary>
        public bool EnumAsInteger { get; }
    }
}
