namespace LiteDB
{
    /// <summary>
    /// Selects how documents are written while retaining support for reading
    /// both BSON and compact documents.
    /// </summary>
    public enum CompactStorageMode
    {
        /// <summary>
        /// Use compact writes when beneficial. New databases start at v10;
        /// existing v8/v9 databases are promoted on their first compact write.
        /// </summary>
        Auto = 0,

        /// <summary>Always write legacy BSON documents.</summary>
        Legacy = 1,

        /// <summary>
        /// Use compact writes when beneficial, lazily promoting existing
        /// v8/v9 databases to v10 on the first compact write.
        /// </summary>
        Compact = 2
    }
}
