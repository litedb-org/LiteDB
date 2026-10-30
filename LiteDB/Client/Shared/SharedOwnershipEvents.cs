using System;
using System.Diagnostics;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// TESTING-only observation of Shared writer protection: the lifecycle of every core that
    /// requires writer exclusion (opened, closing, closed) and every release of a connection's
    /// native writer mutex. Oracles use it to check that a core active or still tearing down
    /// keeps its protection, and that protection is released only after that core's teardown
    /// completed. Production builds drop the calls and the observers.
    /// </summary>
    internal static class SharedOwnershipEvents
    {
        internal const string Opened = "opened";
        internal const string Closing = "closing";
        internal const string Closed = "closed";

#if DEBUG || TESTING
        /// <summary>Observer: (connection, core, stage) for cores that require writer exclusion.</summary>
        internal static Action<SharedEngine, LiteEngine, string> CoreStage;

        /// <summary>Observer: runs on the releasing thread just before it releases this native mutex.</summary>
        internal static Action<Mutex> Releasing;
#endif

        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Core(SharedEngine connection, LiteEngine core, string stage)
        {
#if DEBUG || TESTING
            Volatile.Read(ref CoreStage)?.Invoke(connection, core, stage);
#endif
        }

        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Release(Mutex mutex)
        {
#if DEBUG || TESTING
            Volatile.Read(ref Releasing)?.Invoke(mutex);
#endif
        }
    }
}
