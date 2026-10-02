using System;
using System.Diagnostics;
#if DEBUG || TESTING
using System.Threading;
#endif

namespace LiteDB.Utils
{
    /// <summary>
    /// Step markers of registered teardown paths (<see cref="TeardownPathAttribute"/>). A step is one
    /// action that can fail (I/O, a callback, a wait that rethrows); <see cref="Before"/> and
    /// <see cref="After"/> bracket it inside the block whose handling a real failure of that action
    /// would get. A sweep arms one fault per scenario: the <c>skip</c> model throws at Before (the
    /// action never runs), the <c>fail-inside</c> model throws at After (the action ran, then failed).
    /// Steps inside <see cref="TryCatch"/> are named with <see cref="TryCatch.Step"/> instead. Names are
    /// literal, <c>&lt;Path&gt;.&lt;step&gt;</c>, and registered in .github/safety/fault-points.json
    /// (family <c>teardown-step</c>); each Before counts the <c>fault-point:&lt;name&gt;</c> marker.
    /// Production builds remove the calls and their arguments.
    /// </summary>
    internal static class TeardownSteps
    {
        /// <summary>The step's action is about to run; <paramref name="applies"/> false when there is no action this time.</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Before(string step, bool applies = true)
        {
#if DEBUG || TESTING
            if (!applies) return;
            Reachability.FaultPoint(step);
            Reach(step, TeardownStepSite.Before);
#endif
        }

        /// <summary>The step's action returned.</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void After(string step, bool applies = true)
        {
#if DEBUG || TESTING
            if (!applies) return;
            Reach(step, TeardownStepSite.After);
#endif
        }

#if DEBUG || TESTING
        private static readonly AsyncLocal<TeardownScenario> _scenario = new AsyncLocal<TeardownScenario>();

        /// <summary>The sweep scenario of the current execution context; null outside a sweep.</summary>
        internal static TeardownScenario Current => _scenario.Value;

        /// <summary>
        /// Make <paramref name="scenario"/> current for this execution context and everything it
        /// starts (threads and tasks capture it), so steps of holder threads count for it too and a
        /// concurrent test's steps never do. Begin before the scenario opens its connections.
        /// </summary>
        internal static IDisposable Begin(TeardownScenario scenario)
        {
            var previous = _scenario.Value;
            _scenario.Value = scenario;
            return new Restore(previous, scenario);
        }

        /// <summary>A step site was reached: record it, and throw the scenario's armed fault there.</summary>
        internal static void Reach(string step, TeardownStepSite site) => _scenario.Value?.Reached(step, site);

        private sealed class Restore : IDisposable
        {
            private readonly TeardownScenario _previous;
            private readonly TeardownScenario _ended;

            public Restore(TeardownScenario previous, TeardownScenario ended)
            {
                _previous = previous;
                _ended = ended;
            }

            public void Dispose()
            {
                // Threads the scenario started keep a reference; a closed scenario ignores them.
                _ended.Close();
                _scenario.Value = _previous;
            }
        }
#endif
    }
}
