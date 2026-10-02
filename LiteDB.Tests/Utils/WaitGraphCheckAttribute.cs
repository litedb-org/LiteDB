using System;
using System.Linq;
using System.Reflection;
using LiteDB.Utils;
using Xunit.Sdk;

[assembly: LiteDB.Tests.WaitGraphCheck]

namespace LiteDB.Tests
{
    /// <summary>
    /// Applied to the whole test assembly: attributes wait-for graph findings to the running test
    /// (<see cref="WaitGraph.Context"/>) and prints those it latched, including cycles whose waits
    /// later timed out or hung. A test fails here only for a finding of a failing rule: by default the
    /// proven rules in <see cref="WaitGraph.DefaultFailing"/> (self-wait, unbounded cycle), else as configured by
    /// <c>LITEDB_WAITGRAPH_FAIL</c> or <see cref="WaitGraph.SetFailing"/>. Tests run one at a time (xunit.runner.json), so a finding belongs to the running test;
    /// a background thread left by an earlier test is attributed to the test during which it waited.
    /// See docs/wait-for-graph.md.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public sealed class WaitGraphCheckAttribute : BeforeAfterTestAttribute
    {
        private static int _findings;

        /// <summary>Number of tests this hook has seen start; the graph's own tests check that it runs.</summary>
        internal static int Started;

        public override void Before(MethodInfo methodUnderTest)
        {
            // The sequence number tells theory cases apart (xUnit 2 does not pass their arguments here).
            var sequence = System.Threading.Interlocked.Increment(ref Started);
            WaitGraph.Context = methodUnderTest.DeclaringType?.FullName + "." + methodUnderTest.Name + " #" + sequence;
            _findings = WaitGraph.Findings.Count;
        }

        public override void After(MethodInfo methodUnderTest)
        {
            var findings = WaitGraph.Findings.Skip(_findings).ToArray();
            foreach (var finding in findings)
                Console.WriteLine($"[wait-for graph] {WaitGraph.Context} {finding}");
            try
            {
                WaitGraph.ThrowIfFailing(findings);
            }
            catch (DeadlockDetectedException ex)
            {
                throw new XunitException("The wait-for graph latched a finding of a failing rule during this test:" +
                    Environment.NewLine + ex.Findings);
            }
        }
    }
}
