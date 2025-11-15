namespace LiteDB
{
    /// <summary>
    /// Specifies the auto-ID generation strategy for documents that do not contain an <c>_id</c> field.
    /// </summary>
    /// <remarks>
    /// When inserting a document without an <c>_id</c> field, LiteDB will automatically generate one based on the specified strategy.
    /// The numeric values correspond to the internal BSON type identifiers.
    /// </remarks>
    public enum BsonAutoId
    {
        /// <summary>
        /// Generates a 32-bit integer auto-ID using sequential numbering. Starts at 1 for empty collections, otherwise continues from the highest existing value + 1.
        /// </summary>
        Int32 = 2,

        /// <summary>
        /// Generates a 64-bit integer auto-ID using sequential numbering. Starts at 1 for empty collections, otherwise continues from the highest existing value + 1.
        /// </summary>
        Int64 = 3,

        /// <summary>
        /// Generates a 12-byte <see cref="ObjectId"/> auto-ID. This is the default strategy.
        /// ObjectIds are globally unique, sortable by creation time, and include timestamp, machine ID, process ID, and counter components.
        /// </summary>
        ObjectId = 10,

        /// <summary>
        /// Generates a globally unique identifier (GUID/UUID) auto-ID using <see cref="System.Guid.NewGuid"/>.
        /// </summary>
        Guid = 11
    }
}