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
        /// The latest data sync of an engine of this connection answered "cannot sync": its WAL is
        /// kept until one succeeds. Its checkpoints retry the data sync first (DefersCheckpoint), and
        /// <c>$database.walKept</c> of a read-only engine, which never syncs, reports it.
        /// </summary>
        internal volatile bool DataUnsynced;
    }
}
