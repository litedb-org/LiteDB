using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Utils
{
    /// <summary>
    /// Reachability ("sometimes") markers: a situation a campaign must reach at least once.
    /// A marker counts its hits; it never changes behavior. Names are
    /// <c>&lt;family&gt;:&lt;name&gt;</c> (<c>fault-point</c>, <c>maintenance</c>, <c>refusal</c>,
    /// <c>api</c>, <c>situation</c>) and every literal name is registered in
    /// <c>.github/safety/markers.json</c>. Production builds remove the calls and their
    /// arguments: both methods are conditional on <c>DEBUG</c> or <c>TESTING</c>.
    /// </summary>
    internal static class Reachability
    {
#if DEBUG || TESTING
        private static ConcurrentDictionary<string, long[]> _hits = NewCounters();

        /// <summary>
        /// Test observer: runs on the hitting thread after the hit is counted, for example to
        /// evaluate an invariant at every fault point. It must not throw or block.
        /// </summary>
        internal static Action<string> Observer;
#endif

        /// <summary>Count one occurrence of <paramref name="situation"/>.</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Sometimes(string situation)
        {
#if DEBUG || TESTING
            Interlocked.Increment(ref Volatile.Read(ref _hits).GetOrAdd(situation, _ => new long[1])[0]);
            Volatile.Read(ref Observer)?.Invoke(situation);
#endif
        }

        /// <summary>
        /// The site of the registered fault hook <paramref name="name"/> executed, whether or
        /// not a test installed the hook. Counted as <c>fault-point:&lt;name&gt;</c>.
        /// </summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void FaultPoint(string name)
        {
#if DEBUG || TESTING
            Sometimes("fault-point:" + name);
#endif
        }

#if DEBUG || TESTING
        /// <summary>Hit counts since the last <see cref="Reset"/>, by marker name.</summary>
        internal static IDictionary<string, long> Snapshot()
        {
            var hits = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var pair in Volatile.Read(ref _hits)) hits[pair.Key] = Interlocked.Read(ref pair.Value[0]);
            return hits;
        }

        internal static void Reset() => Volatile.Write(ref _hits, NewCounters());

        private static ConcurrentDictionary<string, long[]> NewCounters() =>
            new ConcurrentDictionary<string, long[]>(StringComparer.Ordinal);
#endif
    }
}
