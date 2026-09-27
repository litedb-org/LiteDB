#if !NETFRAMEWORK && (DEBUG || TESTING)
using System;
using LiteDB.Client.Coordinated;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// For tests that count page hits and incremental refreshes: a loaded runner can exceed the
    /// 500 ms snapshot idle time or the 1 s heartbeat trust window between two reads, and the
    /// client then correctly takes the slower path (a fresh snapshot or an IPC grant), which such
    /// a count does not expect. Coordinator tests run one at a time, so the scope is exclusive.
    /// Crash and takeover tests keep the real timing.
    /// </summary>
    internal static class CoordinatorTiming
    {
        internal static IDisposable Relaxed()
        {
            CoordinatorClient.KeepIdleSnapshots = true;
            CoordinatorStatusPage.TrustTimeoutMilliseconds = 60_000;
            return new Restore();
        }

        private sealed class Restore : IDisposable
        {
            public void Dispose()
            {
                CoordinatorClient.KeepIdleSnapshots = false;
                CoordinatorStatusPage.TrustTimeoutMilliseconds = CoordinatorStatusPage.HeartbeatTimeoutMilliseconds;
            }
        }
    }
}
#endif
