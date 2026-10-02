using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// What a code path promises to do with a failure injected into it. Each path declares its
    /// contract (a teardown path's registry declaration, or the scenario that drives the call):
    /// upstream <c>LiteEngine.Dispose</c>, for example, calls <c>Close()</c> and discards the
    /// failure list it returns, so it declares <see cref="Discarded"/>.
    /// </summary>
    [Flags]
    internal enum FaultDisposition
    {
        None = 0,
        /// <summary>The call throws the injected exception, or one whose cause chain carries it.</summary>
        Propagated = 1,
        /// <summary>The call returns normally and its returned failure list carries the fault.</summary>
        ReturnedAsFailureList = 2,
        /// <summary>The call throws another (primary) error and records the fault as a secondary cleanup error.</summary>
        RecordedAsCleanupError = 4,
        /// <summary>The call retried past the fault and completed normally.</summary>
        Retried = 8,
        /// <summary>The call throws the caller's primary error unchanged; the fault is suppressed.</summary>
        SuppressedPreservingPrimary = 16,
        /// <summary>The call returns normally and nothing reports the fault.</summary>
        Discarded = 32
    }

    /// <summary>How an injector fails an action: after running it, or instead of running it.</summary>
    internal enum FaultModel
    {
        /// <summary>The action runs, then the fault is thrown: its effects happened.</summary>
        FailInside,
        /// <summary>The fault is thrown before the action, which never runs.</summary>
        Skip
    }

    /// <summary>One injected fault: its model, the exception it threw, and whether it fired.</summary>
    internal sealed class FaultInjection
    {
        public FaultInjection(string name, FaultModel model)
        {
            this.Name = name;
            this.Model = model;
        }

        public string Name { get; }
        public FaultModel Model { get; }
        public Exception Injected { get; private set; }
        public bool Fired => this.Injected != null;

        /// <summary>Run <paramref name="action"/> under this injection: once, with the configured model.</summary>
        public void Run(Action action, Func<Exception> fault)
        {
            if (this.Fired)
            {
                action();
                return;
            }
            if (this.Model == FaultModel.FailInside) action();
            throw this.Injected = fault();
        }

        /// <summary>Record a fault thrown by an injector the harness does not wrap (a throwing stream).</summary>
        public void Record(Exception injected) => this.Injected = this.Injected ?? injected;
    }

    /// <summary>
    /// FaultDisposed: classifies what a call did with an injected fault that fired, so the result can
    /// be compared with the path's declared <see cref="FaultDisposition"/>. A swallowed fault on a
    /// path that declares Propagated, or a replaced primary error, is a mismatch.
    /// </summary>
    internal static class FaultDisposedProbe
    {
        /// <param name="injected">The fault that fired.</param>
        /// <param name="thrown">What the call threw; null when it returned.</param>
        /// <param name="returned">The failure list the call returned, if it returns one.</param>
        /// <param name="primary">The caller's own primary error, when the call ran during its handling.</param>
        /// <param name="retried">The harness observed the call retry past the fault.</param>
        public static FaultDisposition Observe(Exception injected, Exception thrown, IEnumerable<Exception> returned = null,
            Exception primary = null, bool retried = false)
        {
            if (thrown == null)
            {
                if (returned != null && returned.Any(item => Carries(item, injected, false, 0))) return FaultDisposition.ReturnedAsFailureList;
                return retried ? FaultDisposition.Retried : FaultDisposition.Discarded;
            }
            if (Carries(thrown, injected, false, 0)) return FaultDisposition.Propagated;
            if (Carries(thrown, injected, true, 0)) return FaultDisposition.RecordedAsCleanupError;
            if (primary != null && Carries(thrown, primary, false, 0)) return FaultDisposition.SuppressedPreservingPrimary;
            return FaultDisposition.None; // replaced: neither the fault nor the primary error surfaced
        }

        /// <summary>
        /// True when <paramref name="thrown"/> is <paramref name="target"/> or has it in its cause chain
        /// (InnerException, AggregateException members); with <paramref name="secondary"/>, also when it
        /// is recorded as a secondary error (a value in Exception.Data, or a later aggregate member).
        /// </summary>
        public static bool Carries(Exception thrown, Exception target, bool secondary, int depth)
        {
            if (thrown == null || depth > 32) return false;
            if (ReferenceEquals(thrown, target)) return true;
            if (thrown is AggregateException aggregate)
            {
                var members = aggregate.InnerExceptions;
                for (var i = 0; i < members.Count; i++)
                    if ((i == 0 || secondary) && Carries(members[i], target, secondary, depth + 1)) return true;
            }
            if (secondary)
            {
                foreach (DictionaryEntry entry in thrown.Data)
                    if (entry.Value is Exception carried && Carries(carried, target, true, depth + 1)) return true;
            }
            return Carries(thrown.InnerException, target, secondary, depth + 1);
        }
    }
}
