namespace LiteDB
{
    /// <summary>
    /// Predictable per-database memory defaults. Explicit cache and transaction
    /// limits override the profile. Profiles do not inspect host RAM or CPU count.
    /// </summary>
    public enum MemoryProfile
    {
        /// <summary>
        /// General-purpose default: 64 MiB file cache (8 MiB in memory),
        /// with a cooperative transaction threshold of 1,000 pages.
        /// </summary>
        Balanced = 0,

        /// <summary>
        /// Small hosts or many databases: 8 MiB file cache (4 MiB in memory),
        /// with a cooperative transaction threshold of 256 pages.
        /// </summary>
        LowMemory = 1,

        /// <summary>
        /// Larger working sets and bulk writes: 128 MiB file cache (16 MiB
        /// in memory), with a cooperative transaction threshold of 4,000 pages.
        /// </summary>
        Throughput = 2
    }
}
