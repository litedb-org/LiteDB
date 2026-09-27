namespace LiteDB.Engine
{
    /// <summary>
    /// Experimental coordinator: the coordinator's engine reports the events that a
    /// client's direct snapshot open must not overlap (see docs/experimental-coordinator.md).
    /// Callbacks run on engine threads, possibly under engine locks; they must not call
    /// back into the engine.
    /// </summary>
    internal interface ICoordinationSignals
    {
        /// <summary>
        /// A checkpoint, format promotion or header rewrite starts. Raised before a
        /// checkpoint scans the shared-reader leases and before any data or WAL byte
        /// outside the append position changes.
        /// </summary>
        void StructuralBegin();

        /// <summary>The matching end; <paramref name="version"/> is the read version afterwards, or -1 if unchanged.</summary>
        void StructuralEnd(int version);

        /// <summary>A reclaimed WAL slot is about to be overwritten by a new frame.</summary>
        void SlotReused();

        /// <summary>A transaction became visible at <paramref name="version"/>.</summary>
        void Committed(int version);
    }
}
