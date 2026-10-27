using System.Collections.Generic;

namespace LiteDB.Internals
{
    internal static class BackgroundCheckpointFaultMatrix
    {
        // @n selects first/middle/last backfill writes in this bounded workload.
        internal static readonly string[] Full =
        {
            "BeforeCheckpointWorkerPass", "before-commit-lock", "before-barrier-lock", "before-index-lock",
            "checkpoint-before-validation", "checkpoint-after-validation", "checkpoint-before-wal-sync",
            "checkpoint-before-journal-write", "checkpoint-after-journal-write", "checkpoint-after-journal-flush",
            "checkpoint-after-journal-sync", "checkpoint-before-page-write", "data-page", "data-page@4", "data-page@8",
            "checkpoint-after-page-write",
            "checkpoint-before-data-flush", "checkpoint-after-data-flush", "before-reclaim",
            "checkpoint-before-salt-write", "checkpoint-after-salt-write", "checkpoint-after-salt-sync",
            "checkpoint-before-clear", "checkpoint-after-clear", "after-reclaim", "AfterCheckpointWorkerPass"
        };

        internal static readonly string[] Partial =
        {
            "retirement-before-record-write", "retirement-after-record-write", "retirement-records-flushed",
            "retirement-before-header-write", "retirement-after-header-write", "retirement-header-flushed",
            "retirement-before-journal-retire-flush", "retirement-journal-retired",
            "before-wal-reclaim-lock", "wal-slot-cleared", "wal-slots-flushed", "wal-slots-published"
        };

        internal static IEnumerable<object[]> ProcessCases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var durable in new[] { false, true })
            {
                foreach (var stage in Full) yield return new object[] { password, durable, stage, false };
                foreach (var stage in Partial) yield return new object[] { password, durable, stage, true };
            }
        }
    }
}
