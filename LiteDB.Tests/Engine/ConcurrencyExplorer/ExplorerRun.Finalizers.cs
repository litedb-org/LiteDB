using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>Finalizer flush and page buffer audit at the end of every explorer run.</summary>
    internal sealed partial class ExplorerRun
    {
        /// <summary>
        /// Runs pending finalizers now. A finalizer that fails (a page buffer whose share count is not
        /// zero) terminates a TESTING process; flushing after every run attributes that crash to the
        /// run whose connections leaked or over-released the buffer (its history ends with this event).
        /// </summary>
        internal static void FlushFinalizers()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static readonly ConcurrentQueue<int> FinalizedInUse = new ConcurrentQueue<int>();
        private static volatile bool _auditing;

        /// <summary>
        /// Single-target processes (the fuzz targets): observe page buffers finalized with a non-zero share
        /// count through the registered TESTING hook PageBuffer.FinalizedInUse instead of letting the
        /// finalizer's ENSURE terminate the process; each run then fails with
        /// EXPLORER_PAGE_BUFFER_FINALIZED_IN_USE when its flush finds one. Not for xUnit, where other
        /// tests assign the same static hook.
        /// </summary>
        internal static void AuditPageBuffers()
        {
            _auditing = true;
            LiteDB.Engine.PageBuffer.FinalizedInUse = count => FinalizedInUse.Enqueue(count);
        }

        private static string TakeFinalizedInUse()
        {
            var counts = new List<int>();
            while (FinalizedInUse.TryDequeue(out var count)) counts.Add(count);
            return counts.Count == 0 ? null : string.Join(", ", counts);
        }
    }
}
