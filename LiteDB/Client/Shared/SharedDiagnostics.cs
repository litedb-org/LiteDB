namespace LiteDB
{
    /// <summary>A best-effort, process-local observation; never authorizes database access.</summary>
    public sealed class SharedDiagnostics
    {
        /// <summary>The currently observed read path for this connection.</summary>
        public SharedReadPath ReadPath { get; internal set; }
        /// <summary>The last reason mapped attachment was unavailable, if any.</summary>
        public string FallbackReason { get; internal set; }
        /// <summary>Queries admitted using a cached mapped snapshot.</summary>
        public long CoordinatedReadHits { get; internal set; }
        /// <summary>Eligible query attempts requiring the protected path instead.</summary>
        public long CoordinatedReadMisses { get; internal set; }
        /// <summary>Live snapshot leases owned by this connection, including retired snapshots.</summary>
        public int ActiveSnapshotLeases { get; internal set; }
        /// <summary>Mapped participants for this path in this process, not a cross-process census.</summary>
        public int ProcessMappedParticipants { get; internal set; }
        /// <summary>Writer scheduling hints published by this connection.</summary>
        public long WriterPressureRequests { get; internal set; }
        /// <summary>Cached read attempts that yielded to writer pressure.</summary>
        public long WriterYields { get; internal set; }
    }
}
