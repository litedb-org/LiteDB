#if DEBUG || TESTING
using System;
using System.Threading;

namespace LiteDB.Engine
{
    // Assertions throw in test builds so violations are executable failures on
    // every runtime, including Release + TestingEnabled=true.
    internal static class OwnershipAssertions
    {
        internal static void RequireHeld(object gate, string message)
        {
            if (!Monitor.IsEntered(gate)) throw new InvalidOperationException(message);
        }

        internal static void RequireNotHeld(object gate, string message)
        {
            if (Monitor.IsEntered(gate)) throw new InvalidOperationException(message);
        }

        internal static void BeforeTicketWait(HeaderPage header, object writer, int snapshots)
        {
            RequireNotHeld(header, "Release header before waiting for a commit ticket.");
            RequireNotHeld(header.PublicationLock, "Release publication before waiting for a commit ticket.");
            RequireNotHeld(writer, "Release WAL writer before waiting for a commit ticket.");
            if (snapshots != 0) throw new InvalidOperationException("Release collection snapshots before waiting for a commit ticket.");
        }
    }
}
#endif
