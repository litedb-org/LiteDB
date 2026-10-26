#if DEBUG || TESTING
using System;

namespace LiteDB.Utils
{
    /// <summary>
    /// Raised by a test harness at the end of a scenario (<see cref="WaitGraph.ThrowIfFailing"/>) when the
    /// test-build wait-for graph latched a finding of a rule configured to fail. Library code never
    /// throws it: wait sites only record. The message prints each finding: the waiting threads, wait
    /// sites, primitives, bounds, resources, the owners holding them and the threads executing them.
    /// It derives from <see cref="Exception"/> directly so that handlers for LiteDB's own exception
    /// types cannot mistake it for a timeout or refusal.
    /// </summary>
    internal sealed class DeadlockDetectedException : Exception
    {
        internal DeadlockDetectedException(string findings)
            : base(findings)
        {
            this.Findings = findings;
        }

        /// <summary>The printed findings, as also latched in <see cref="WaitGraph.Findings"/>.</summary>
        internal string Findings { get; }
    }
}
#endif
