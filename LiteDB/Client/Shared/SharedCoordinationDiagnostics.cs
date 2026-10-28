using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Admission coordination counter: selected file/range-lock/owner/probe seams.
    // Excludes scheduling (Sleep/Yield/futex), monitor contention, direct native
    // opens/turnstiles and storage page I/O. Zero is not a syscall guarantee.
    // Thread-local accounting excludes unrelated timer/peer work in forced races.
    internal static class SharedCoordinationDiagnostics
    {
#if DEBUG || TESTING
        [ThreadStatic] internal static int AdmissionOperations;
#endif
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Operation()
        {
#if DEBUG || TESTING
            AdmissionOperations++;
#endif
        }
    }
}
