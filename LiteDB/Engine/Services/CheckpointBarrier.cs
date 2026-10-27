using System;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Excludes checkpoint from a group leader's captured handle and durable-prefix
    /// publication. The group enters after its leader lock, without header/index
    /// locks. Checkpoint enters after the header, before the index and WAL writer.
    /// Neither path acquires the other's preceding locks while holding this lease.
    /// </summary>
    internal sealed class CheckpointBarrier
    {
        private readonly object _gate = new object();

        internal Lease Enter()
        {
            Monitor.Enter(_gate);
            return new Lease(_gate);
        }

        internal struct Lease : IDisposable
        {
            private object _gate;

            internal Lease(object gate) => _gate = gate;

            public void Dispose()
            {
                // A monitor lease belongs to its acquiring thread. Inactive
                // engines use the default value without an atomic operation.
                var gate = _gate;
                if (gate == null) return;
                _gate = null;
                Monitor.Exit(gate);
            }
        }
    }
}
