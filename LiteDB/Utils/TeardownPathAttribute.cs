using System;
using System.Diagnostics;

namespace LiteDB.Utils
{
    /// <summary>
    /// What a teardown path promises to do with a fault raised inside its teardown, as its entry
    /// is observed by the caller. Flags, so a path that allows several dispositions declares the set.
    /// The values mirror the test oracle's <c>LiteDB.Tests.Safety.FaultDisposition</c> one to one.
    /// </summary>
    [Flags]
    internal enum TeardownDisposition
    {
        None = 0,
        /// <summary>The entry throws the fault, or an exception whose cause chain carries it.</summary>
        Propagated = 1,
        /// <summary>The entry returns normally and its returned failure list carries the fault.</summary>
        ReturnedAsFailureList = 2,
        /// <summary>The entry throws another (primary) error and records the fault as a secondary cleanup error.</summary>
        RecordedAsCleanupError = 4,
        /// <summary>The entry retried past the fault and completed normally.</summary>
        Retried = 8,
        /// <summary>The entry throws the caller's primary error unchanged; the fault is suppressed.</summary>
        SuppressedPreservingPrimary = 16,
        /// <summary>The entry returns normally and nothing reports the fault.</summary>
        Discarded = 32
    }

    /// <summary>
    /// Registers a teardown path (a Dispose, Close, release or cleanup routine) for the teardown
    /// step-fault sweep, with its declared disposition of a fault during teardown. Every registered
    /// path needs a sweep driver (<c>LiteDB.Tests/Safety/TeardownSweepDrivers*.cs</c>); a path
    /// without one fails the sweep, so a new path is swept as soon as it is declared. Its step markers
    /// (<see cref="TeardownSteps"/>, <see cref="TryCatch.Step"/>) are named <c>&lt;Name&gt;.&lt;step&gt;</c>.
    /// The attribute is conditional: production builds (no <c>DEBUG</c>/<c>TESTING</c>) keep no
    /// usage of it in their metadata. See docs/teardown-sweep.md.
    /// </summary>
    [Conditional("DEBUG"), Conditional("TESTING")]
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class TeardownPathAttribute : Attribute
    {
        /// <param name="name">Stable path name, <c>&lt;Type&gt;.&lt;Member&gt;</c>; prefixes its step names.</param>
        /// <param name="declared">The dispositions the path's entry may give a fault raised inside it.</param>
        /// <param name="basis">Where the declaration comes from: the code and documentation that establish it.</param>
        public TeardownPathAttribute(string name, TeardownDisposition declared, string basis)
        {
            this.Name = name;
            this.Declared = declared;
            this.Basis = basis;
        }

        public string Name { get; }

        public TeardownDisposition Declared { get; }

        public string Basis { get; }
    }
}
