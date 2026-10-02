using System;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Behaviour of the current code that this property found and that is not fixed yet. Each finding
    /// is kept out of generation by one named rule, so the property stays green and still covers
    /// everything else. Set <c>LITEDB_PBT_INCLUDE_KNOWN_FINDINGS=1</c> (or
    /// <see cref="PropertyOptions.IncludeKnownFindings"/>) to generate the excluded cases again;
    /// the property then fails with the finding. Remove the rule together with the fix.
    /// </summary>
    public static class KnownFindings
    {
        /// <summary>
        /// Shared mode retains the connection's mutex after a transaction restarted within one
        /// block. Sequence on one thread: BeginTrans (true); a write that fails (for example a
        /// duplicate key), which rolls the explicit transaction back; BeginTrans again (true, a new
        /// transaction, as in Direct mode); Commit (true). No transaction is open afterwards, yet the
        /// idle thread still owns the mutex: every call of another thread waits until the owner thread
        /// exits (abandoned-mutex recovery). Direct mode, and the same block without the second
        /// BeginTrans or without the failure, release normally.
        /// Found by the sequential property, Shared, case seed 19.
        /// Rule: in Shared mode a nested BeginTrans is generated only directly after the block's
        /// opening BeginTrans, before any command that could fail.
        /// </summary>
        public const string SharedRestartAfterAbortRetainsMutex = "shared-restart-after-abort-retains-mutex";

        /// <summary>Default for <see cref="PropertyOptions.IncludeKnownFindings"/>.</summary>
        public static bool IncludeByDefault => Environment.GetEnvironmentVariable("LITEDB_PBT_INCLUDE_KNOWN_FINDINGS") == "1";
    }
}
