#if DEBUG || TESTING
using System;
using System.Collections.Generic;

namespace LiteDB.Utils
{
    /// <summary>Where a teardown step marker sits relative to the step's action.</summary>
    internal enum TeardownStepSite
    {
        /// <summary>Before the action: a fault thrown here is the <c>skip</c> model.</summary>
        Before,
        /// <summary>After the action: a fault thrown here is the <c>fail-inside</c> model.</summary>
        After
    }

    /// <summary>One step site reached during a scenario's window.</summary>
    internal readonly struct TeardownVisit
    {
        public TeardownVisit(string step, TeardownStepSite site, int occurrence, int thread)
        {
            this.Step = step;
            this.Site = site;
            this.Occurrence = occurrence;
            this.Thread = thread;
        }

        public string Step { get; }
        public TeardownStepSite Site { get; }
        /// <summary>1-based count of this (step, site) within the window.</summary>
        public int Occurrence { get; }
        public int Thread { get; }
    }

    /// <summary>
    /// TESTING-only state of one teardown sweep scenario: the step sites reached while its window is
    /// open (the driver opens it right before invoking the teardown path) and at most one armed fault,
    /// which fires once at the n-th visit of its (step, site) in the window. Thread-safe: holder
    /// threads of the scenario's connections report here too.
    /// </summary>
    internal sealed class TeardownScenario
    {
        private readonly object _sync = new object();
        private readonly List<TeardownVisit> _visits = new List<TeardownVisit>();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _open;
        private bool _closed;
        private string _armedStep;
        private TeardownStepSite _armedSite;
        private int _armedOccurrence;
        private Func<Exception> _fault;

        /// <summary>The fault that fired, if any.</summary>
        public Exception Fired { get; private set; }

        /// <summary>The visit at which the armed fault fired.</summary>
        public TeardownVisit? FiredAt { get; private set; }

        /// <summary>Arm the one fault of this scenario: thrown at the <paramref name="occurrence"/>-th window visit of the site.</summary>
        public void Arm(string step, TeardownStepSite site, int occurrence, Func<Exception> fault)
        {
            lock (_sync)
            {
                if (_fault != null) throw new InvalidOperationException("A teardown scenario arms one fault.");
                _armedStep = step;
                _armedSite = site;
                _armedOccurrence = occurrence;
                _fault = fault ?? throw new ArgumentNullException(nameof(fault));
            }
        }

        /// <summary>Start counting visits (and allow the armed fault to fire); earlier visits belong to setup.</summary>
        public void OpenWindow()
        {
            lock (_sync) _open = true;
        }

        /// <summary>Stop counting; later visits (scenario cleanup, cold reopen) are not part of the path.</summary>
        public void CloseWindow()
        {
            lock (_sync) _open = false;
        }

        /// <summary>The visits of the window so far, in the order they were recorded.</summary>
        public TeardownVisit[] Visits()
        {
            lock (_sync) return _visits.ToArray();
        }

        internal void Close()
        {
            lock (_sync)
            {
                _open = false;
                _closed = true;
            }
        }

        internal void Reached(string step, TeardownStepSite site)
        {
            Exception fault = null;
            lock (_sync)
            {
                if (!_open || _closed) return;
                var key = step + (site == TeardownStepSite.Before ? "|before" : "|after");
                _counts.TryGetValue(key, out var count);
                _counts[key] = ++count;
                var visit = new TeardownVisit(step, site, count, Environment.CurrentManagedThreadId);
                _visits.Add(visit);
                if (_fault != null && this.Fired == null && site == _armedSite && count == _armedOccurrence &&
                    string.Equals(step, _armedStep, StringComparison.Ordinal))
                {
                    fault = this.Fired = _fault();
                    this.FiredAt = visit;
                }
            }
            if (fault != null) throw fault;
        }
    }
}
#endif
