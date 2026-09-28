namespace LiteDB.Engine
{
    /// <summary>
    /// A shared connection's diagnostic survives its short-lived file engines.
    /// This does not suppress subsequent engines' attempts to sync to the device.
    /// </summary>
    internal sealed class SharedDurabilityState
    {
        internal volatile bool Degraded;

        /// <summary>
        /// A data or log file answered "cannot sync" (#2242). Later engines cannot tell whether
        /// earlier checkpoints reached the device, so they neither retire nor reuse WAL frames.
        /// </summary>
        internal volatile bool FileSyncUnsupported;

        /// <summary>
        /// The data header as of the connection's latest successful data file sync. A later engine
        /// that finds this exact header needs no sync of its own before its first durable commit
        /// (see DiskService.ProveDataFile).
        /// </summary>
        internal volatile byte[] DurableHeader;
    }
}
