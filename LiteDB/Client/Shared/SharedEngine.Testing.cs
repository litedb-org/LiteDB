using System;
using System.Diagnostics;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB
{
    /// <summary>Test hooks and reachability markers of a Shared connection; none exist in production builds.</summary>
    public partial class SharedEngine
    {
#if DEBUG || TESTING
        internal Func<LiteEngine> SimulateOpenEngine { get; set; }

        /// <summary>Test hook: runs in OpenDatabase between the engine check and counting the user.</summary>
        internal Action BeforeCountingUser { get; set; }

        internal int EngineOpens { get; private set; }

        internal int SnapshotOpens { get; private set; }

        internal SharedMutexOwner MutexOwner => _owner;
        internal SharedFileHandles FileHandles => _handles;
#endif

        /// <summary>
        /// Reachability: what a Dispose overlaps when it starts, read once the pin it must end is
        /// known: threads of this connection waiting for the writer mutex, a pin, and an open
        /// explicit transaction (which the final close discards).
        /// </summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void MarkDisposeOverlaps(SharedMutexPin pin)
        {
#if DEBUG || TESTING
            if (this.HasMutexWaiters()) Reachability.Sometimes("maintenance:shared-dispose-with-mutex-waiters");
            if (pin != null) Reachability.Sometimes("maintenance:shared-dispose-during-pin");
            if (_transactionRunning) Reachability.Sometimes("maintenance:shared-dispose-during-transaction");
#endif
        }
    }
}
