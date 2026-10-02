using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.Specifications;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// Liveness: every operation completes or is rejected. Hot while any operation is
    /// pending, so Coyote also reports an execution that ends (all actors idle) in that state.
    /// The world reports a stuck model explicitly, with every blocked thread's location.
    /// </summary>
    internal sealed class LifetimeLivenessMonitor : Monitor
    {
        internal sealed class OpBegan : Event
        {
            public OpBegan(string description, int id)
            {
                this.Description = description;
                this.Id = id;
            }

            public string Description { get; }

            public int Id { get; }
        }

        internal sealed class OpEnded : Event
        {
            public OpEnded(int id) => this.Id = id;

            public int Id { get; }
        }

        internal sealed class Stuck : Event
        {
            public Stuck(string report) => this.Report = report;

            public string Report { get; }
        }

        internal sealed class Finished : Event
        {
        }

        private readonly Dictionary<int, string> _pending = new Dictionary<int, string>();

        [Start]
        [Cold]
        [OnEventDoAction(typeof(OpBegan), nameof(OnBegan))]
        [OnEventDoAction(typeof(OpEnded), nameof(OnEnded))]
        [OnEventDoAction(typeof(Stuck), nameof(OnStuck))]
        [OnEventDoAction(typeof(Finished), nameof(OnFinished))]
        private class Idle : State
        {
        }

        [Hot]
        [OnEventDoAction(typeof(OpBegan), nameof(OnBegan))]
        [OnEventDoAction(typeof(OpEnded), nameof(OnEnded))]
        [OnEventDoAction(typeof(Stuck), nameof(OnStuck))]
        [OnEventDoAction(typeof(Finished), nameof(OnFinished))]
        private class Pending : State
        {
        }

        private void OnBegan(Event e)
        {
            var began = (OpBegan)e;
            _pending[began.Id] = began.Description;
            this.RaiseGotoStateEvent<Pending>();
        }

        private void OnEnded(Event e)
        {
            _pending.Remove(((OpEnded)e).Id);
            if (_pending.Count == 0) this.RaiseGotoStateEvent<Idle>();
        }

        private void OnStuck(Event e)
        {
            this.Assert(_pending.Count == 0, "{0}",
                "Liveness violated: every operation completes or is rejected. No thread can make progress and no bounded " +
                "wait is left to expire, but these operations never ended: " + string.Join("; ", _pending.Values) +
                ((Stuck)e).Report);
        }

        private void OnFinished()
        {
            this.Assert(_pending.Count == 0, "{0}", "Liveness violated: the scenario finished with pending operations: " +
                string.Join("; ", _pending.Values));
        }
    }
}
