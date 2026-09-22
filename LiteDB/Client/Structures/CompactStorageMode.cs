namespace LiteDB
{
    /// <summary>
    /// Selects how documents are written while retaining support for reading
    /// both BSON and compact documents.
    /// </summary>
    public enum CompactStorageMode
    {
        /// <summary>
        /// Use compact writes when beneficial. New databases start at v12;
        /// existing v11 databases are promoted on their first compact write.
        /// </summary>
        Auto = 0,

        /// <summary>Always write BSON documents; checksums and current index ordering remain required.</summary>
        Legacy = 1,

        /// <summary>
        /// Use compact writes when beneficial, lazily promoting existing
        /// v11 databases to v12 on the first compact write.
        /// </summary>
        Compact = 2
    }
}
