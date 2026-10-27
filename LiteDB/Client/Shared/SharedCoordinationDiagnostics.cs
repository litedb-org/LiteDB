using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Count attempted coordination operations at the actual I/O/lock seams.
    // Thread-local accounting excludes unrelated timer/peer work in forced races.
    internal static class SharedCoordinationDiagnostics
    {
#if DEBUG || TESTING
        [ThreadStatic] internal static int Operations;
#endif
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Operation()
        {
#if DEBUG || TESTING
            Operations++;
#endif
        }
    }
}
