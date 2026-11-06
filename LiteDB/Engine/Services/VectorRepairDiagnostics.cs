using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    internal static class VectorRepairDiagnostics
    {
        internal static void Report(string collection, string index, int budget)
        {
            // Trace listeners are application callbacks: never invoke them inline
            // while a document and its graph are between mutation boundaries.
            // Only an immutable message crosses threads, without writer context.
            try
            {
                var message = $"Vector index '{collection}.{index}' repair deferred (base budget {budget}); " +
                    "queries use exact document scans until repaired.";
                ThreadPool.UnsafeQueueUserWorkItem(Write, message);
            }
            catch { }
        }

        private static void Write(object state)
        {
            // Diagnostics and application listener failures cannot abort writes
            // or escape as unhandled exceptions on a ThreadPool worker.
            try { Trace.TraceWarning((string)state); }
            catch { }
        }
    }
}
