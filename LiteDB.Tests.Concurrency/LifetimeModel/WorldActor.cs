using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Coyote;
using Microsoft.Coyote.Actors;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// Interprets the abstract threads of one model iteration. Each thread is a Coyote actor
    /// that asks this world to run its next atomic step, so Coyote decides the interleaving.
    /// Waits block a thread until their condition holds; bounded waits expire only when the
    /// whole model is stuck (discrete time: the earliest deadline fires first, ties are a
    /// controlled choice). A stuck model without bounded waits is reported to the liveness
    /// monitor with every blocked thread and its source location.
    /// </summary>
    internal sealed class WorldActor : StateMachine, IModelHost, ILedgerSink
    {
        internal sealed class Setup : Event
        {
            public Setup(Func<LifetimeModel> factory) => this.Factory = factory;

            public Func<LifetimeModel> Factory { get; }
        }

        internal sealed class StepRequest : Event
        {
            public StepRequest(int thread) => this.Thread = thread;

            public int Thread { get; }
        }

        internal sealed class Go : Event
        {
        }

        private const int MaxTimeouts = 400;

        private readonly List<ModelThread> _threads = new List<ModelThread>();
        private readonly Dictionary<int, ActorId> _actors = new Dictionary<int, ActorId>();
        private readonly HashSet<int> _daemons = new HashSet<int>();
        private readonly HashSet<int> _timeoutPending = new HashSet<int>();
        private readonly List<string> _trace = new List<string>();
        private LifetimeModel _model;
        private LifetimeLedger _ledger;
        private long _now;
        private int _inFlight;
        private int _timeouts;
        private bool _finished;

        public LifetimeLedger Ledger => _ledger;

        [Start]
        [OnEntry(nameof(OnSetup))]
        [OnEventDoAction(typeof(StepRequest), nameof(OnStep))]
        private class Running : State
        {
        }

        private void OnSetup(Event e)
        {
            _ledger = new LifetimeLedger(this);
            _model = ((Setup)e).Factory();
            _model.Attach(this);
            _model.Build();
            this.CheckQuiescence();
        }

        public ModelThread Spawn(string name, Func<ModelThread, IEnumerable<Step>> program, bool daemon = false)
        {
            var thread = new ModelThread(_threads.Count + 1, name) { Status = ThreadStatus.Runnable };
            thread.Program = program(thread).GetEnumerator();
            _threads.Add(thread);
            if (daemon) _daemons.Add(thread.Id);
            _inFlight++;
            _actors[thread.Id] = this.CreateActor(typeof(ThreadActor), new ThreadActor.Bind(this.Id, thread.Id));
            this.Trace($"spawn {name}{(daemon ? " (daemon)" : "")}");
            return thread;
        }

        public int Choose(int count) => count <= 1 ? 0 : this.RandomInteger(count);

        public bool ChooseBool() => this.RandomBoolean();

        public void Trace(string message) => _trace.Add($"[t={_now}ms] {message}");

        void IModelHost.Assert(bool condition, string message) => this.Check(condition, message);

        void ILedgerSink.Assert(bool condition, string message) => this.Check(condition, message);

        private void Check(bool condition, string message)
        {
            if (condition) return;
            this.Assert(false, "{0}", message + this.Report());
        }

        public void OpBegan(ModelOp op) => this.Monitor<LifetimeLivenessMonitor>(new LifetimeLivenessMonitor.OpBegan(op.ToString(), op.Id));

        public void OpEnded(ModelOp op) => this.Monitor<LifetimeLivenessMonitor>(new LifetimeLivenessMonitor.OpEnded(op.Id));

        private void OnStep(Event e)
        {
            var thread = _threads[((StepRequest)e).Thread - 1];
            _inFlight--;
            this.Execute(thread);
            this.WakeReady();
            this.CheckQuiescence();
        }

        private void Execute(ModelThread thread)
        {
            if (thread.Waiting != null)
            {
                if (_timeoutPending.Remove(thread.Id))
                {
                    thread.TimedOut = true;
                    this.Trace($"{thread.Name}: timed out at {thread.Waiting.Site}");
                }
                else if (thread.Waiting.Ready())
                {
                    thread.TimedOut = false;
                }
                else
                {
                    this.Block(thread, thread.Waiting);
                    return;
                }
                thread.Waiting = null;
                thread.Deadline = null;
            }

            while (true)
            {
                bool next;
                try
                {
                    next = thread.Program.MoveNext();
                }
                catch (Exception ex)
                {
                    this.Check(false, $"Model error on {thread.Name} after {thread.LastSite}: {ex}");
                    return;
                }
                if (!next)
                {
                    thread.Status = ThreadStatus.Done;
                    this.Trace($"{thread.Name}: exits");
                    return;
                }

                var step = thread.Program.Current;
                thread.LastSite = step.Site;
                if (step is WaitStep wait)
                {
                    if (wait.Ready())
                    {
                        thread.TimedOut = false;
                        continue;
                    }
                    if (wait.TimeoutMilliseconds.HasValue) thread.Deadline = _now + wait.TimeoutMilliseconds.Value;
                    this.Block(thread, wait);
                    return;
                }

                this.Trace($"{thread.Name}: {step.Site}");
                this.Resume(thread);
                return;
            }
        }

        private void Block(ModelThread thread, WaitStep wait)
        {
            thread.Waiting = wait;
            thread.Status = ThreadStatus.Blocked;
            this.Trace($"{thread.Name}: blocked at {wait.Site}{(thread.Deadline.HasValue ? $" (deadline t={thread.Deadline}ms)" : " (unbounded)")}");
        }

        private void Resume(ModelThread thread)
        {
            thread.Status = ThreadStatus.Runnable;
            _inFlight++;
            this.SendEvent(_actors[thread.Id], new Go());
        }

        private void WakeReady()
        {
            foreach (var thread in _threads)
            {
                if (thread.Status == ThreadStatus.Blocked && thread.Waiting.Ready()) this.Resume(thread);
            }
        }

        private void CheckQuiescence()
        {
            if (_inFlight > 0 || _finished) return;

            var blocked = _threads.Where(t => t.Status == ThreadStatus.Blocked).ToList();
            var pending = _ledger.Pending.ToList();
            if (pending.Count == 0 && blocked.All(t => _daemons.Contains(t.Id)))
            {
                this.Finish();
                return;
            }

            var timed = blocked.Where(t => t.Deadline.HasValue).ToList();
            if (timed.Count > 0)
            {
                this.Check(++_timeouts <= MaxTimeouts, "Model livelock: bounded waits keep expiring without the scenario ending.");
                var earliest = timed.Min(t => t.Deadline.Value);
                var due = timed.Where(t => t.Deadline.Value == earliest).ToList();
                var chosen = due[this.Choose(due.Count)];
                _now = earliest;
                _timeoutPending.Add(chosen.Id);
                this.Resume(chosen);
                return;
            }

            // Stuck with no bound left to expire.
            var closes = pending.Where(o => o.Kind == OpKind.Close || o.Kind == OpKind.Rebuild).ToList();
            if (closes.Count > 0 && !_ledger.Active.Any())
            {
                this.Check(false, $"Safety violated: close terminates once active work returns. {string.Join("; ", closes)} " +
                    "cannot finish although no admitted operation is still active.");
            }
            _finished = true;
            this.Monitor<LifetimeLivenessMonitor>(new LifetimeLivenessMonitor.Stuck(this.Report()));
        }

        private void Finish()
        {
            _finished = true;
            var leftovers = _threads.Where(t => t.Status != ThreadStatus.Done && !_daemons.Contains(t.Id)).ToList();
            this.Check(leftovers.Count == 0, $"Model error: threads {string.Join(", ", leftovers)} did not exit although every operation ended.");
            this.Trace("scenario finished");
            this.Monitor<LifetimeLivenessMonitor>(new LifetimeLivenessMonitor.Finished());
        }

        private string Report()
        {
            var text = new StringBuilder();
            text.AppendLine().AppendLine("Operations:");
            foreach (var op in _ledger.Ops) text.AppendLine("  " + op);
            text.AppendLine("Threads:");
            foreach (var thread in _threads)
            {
                var where = thread.Status == ThreadStatus.Blocked ? $"blocked at {thread.Waiting.Site}" : thread.Status.ToString().ToLowerInvariant();
                text.AppendLine($"  {thread.Name}{(_daemons.Contains(thread.Id) ? " (daemon)" : "")}: {where}");
            }
            var state = _model?.DescribeState();
            if (!string.IsNullOrEmpty(state)) text.AppendLine("State: " + state);
            text.AppendLine("Model trace:");
            foreach (var line in _trace) text.AppendLine("  " + line);
            return text.ToString();
        }
    }

    /// <summary>An abstract thread: it only asks the world to run its next step.</summary>
    internal sealed class ThreadActor : StateMachine
    {
        internal sealed class Bind : Event
        {
            public Bind(ActorId world, int thread)
            {
                this.World = world;
                this.Thread = thread;
            }

            public ActorId World { get; }

            public int Thread { get; }
        }

        private ActorId _world;
        private int _thread;

        [Start]
        [OnEntry(nameof(OnBind))]
        [OnEventDoAction(typeof(WorldActor.Go), nameof(OnGo))]
        private class Ready : State
        {
        }

        private void OnBind(Event e)
        {
            var bind = (Bind)e;
            _world = bind.World;
            _thread = bind.Thread;
            this.SendEvent(_world, new WorldActor.StepRequest(_thread));
        }

        private void OnGo() => this.SendEvent(_world, new WorldActor.StepRequest(_thread));
    }
}
