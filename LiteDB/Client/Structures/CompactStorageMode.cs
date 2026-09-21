namespace LiteDB
{
    /// <summary>
    /// Selects how documents are written while retaining support for reading
    /// both BSON and compact documents.
    /// </summary>
    public enum CompactStorageMode
    {
        /// <summary>
        /// Use compact writes for newly created databases and existing v10
        /// databases. Existing v8/v9 databases continue writing BSON.
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
