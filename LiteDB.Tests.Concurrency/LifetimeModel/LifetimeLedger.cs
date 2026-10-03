using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>The generic operation alphabet shared by every lifetime model.</summary>
    public enum OpKind
    {
        /// <summary>Top-level work on a thread that has nothing admitted.</summary>
        Fresh,
        /// <summary>Work that continues an admitted unit later or on another thread (a streamed reader advanced or disposed).</summary>
        Continuation,
        /// <summary>Work started on a thread inside its own admitted work (same-thread recursion).</summary>
        Nested,
        /// <summary>A transaction owner: begin, work, and complete across several calls of one thread.</summary>
        Owner,
        /// <summary>An active operation whose callback waits for fresh work started on another thread.</summary>
        CallbackDependency,
        Rebuild,
        Close,
        Fatal,
    }

    public enum OpOutcome
    {
        Completed,
        /// <summary>Refused at an admission point (disposed, fenced, admission timeout).</summary>
        Rejected,
        /// <summary>Admitted, then failed (lock contention timeout, closed under it, injected fault).</summary>
        Failed,
    }

    /// <summary>One operation of a scenario and its lifetime.</summary>
    public sealed class ModelOp
    {
        internal ModelOp(int id, OpKind kind, ModelThread thread, string label)
        {
            this.Id = id;
            this.Kind = kind;
            this.Thread = thread;
            this.Label = label;
        }

        public int Id { get; }

        public OpKind Kind { get; }

        public ModelThread Thread { get; }

        public string Label { get; }

        public bool Admitted { get; internal set; }

        public bool Ended => this.Outcome.HasValue;

        public OpOutcome? Outcome { get; internal set; }

        public string Reason { get; internal set; }

        /// <summary>
        /// For an owner (<see cref="OpKind.Owner"/>, or <see cref="OpKind.Nested"/> work inside an
        /// open unit of its thread): the unit it owns (transaction, open reader) can still complete.
        /// </summary>
        public bool TransactionLive { get; internal set; }

        public bool IsMaintenance => this.Kind == OpKind.Rebuild || this.Kind == OpKind.Close || this.Kind == OpKind.Fatal;

        public override string ToString() =>
            $"#{this.Id} {this.Kind}({this.Label}) on {this.Thread.Name}" +
            (this.Ended ? $" -> {this.Outcome}{(this.Reason == null ? "" : ": " + this.Reason)}" : this.Admitted ? " [admitted]" : " [pending]");
    }

    /// <summary>Receives the ledger's observations (the Coyote world actor).</summary>
    public interface ILedgerSink
    {
        void Assert(bool condition, string message);

        void OpBegan(ModelOp op);

        void OpEnded(ModelOp op);

        void Trace(string message);
    }

    /// <summary>
    /// The generic safety properties, stated without reference to any particular state
    /// machine. A model reports what the real code does at the corresponding line; the
    /// ledger decides whether that is allowed.
    /// <list type="bullet">
    /// <item>No fresh admission after close acquired (per fence scope).</item>
    /// <item>No owner rejected while its transaction is still live.</item>
    /// <item>Close terminates once active work returns (checked at quiescence by the world).</item>
    /// </list>
    /// Liveness ("every operation completes or is rejected") is the Coyote monitor's job.
    /// </summary>
    public sealed class LifetimeLedger
    {
        private readonly ILedgerSink _sink;
        private readonly List<ModelOp> _ops = new List<ModelOp>();
        private readonly Dictionary<string, string> _fences = new Dictionary<string, string>();

        public LifetimeLedger(ILedgerSink sink)
        {
            _sink = sink;
        }

        public IReadOnlyList<ModelOp> Ops => _ops;

        public ModelOp Begin(ModelThread thread, OpKind kind, string label)
        {
            var op = new ModelOp(_ops.Count + 1, kind, thread, label);
            _ops.Add(op);
            _sink.OpBegan(op);
            _sink.Trace($"{thread.Name}: begin {op}");
            return op;
        }

        /// <summary>
        /// A close (or the replacement step of a maintenance operation) took effect for
        /// <paramref name="scope"/>: from now on no fresh work may be admitted there.
        /// </summary>
        public void FenceAcquired(string scope, string site)
        {
            if (!_fences.ContainsKey(scope)) _fences[scope] = site;
            _sink.Trace($"fence acquired: {scope} at {site}");
        }

        public bool IsFenced(string scope) => _fences.ContainsKey(scope);

        /// <summary>
        /// <paramref name="op"/> passed its last admission check into <paramref name="scopes"/>
        /// (for example the connection and the engine generation it runs in).
        /// </summary>
        public void Admitted(ModelOp op, string site, params string[] scopes)
        {
            // The first admission of top-level work is fresh; later admissions continue it, and
            // nested work runs inside admitted work of its own thread.
            var fresh = !op.Admitted && op.Kind != OpKind.Nested && !op.IsMaintenance;
            if (fresh)
            {
                foreach (var scope in scopes)
                {
                    _sink.Assert(!_fences.TryGetValue(scope, out var fence),
                        $"Safety violated: no fresh admission after close acquired. {op} was admitted into '{scope}' at {site} " +
                        $"after the close of '{scope}' took effect at {fence}.");
                }
            }
            op.Admitted = true;
            _sink.Trace($"{op.Thread.Name}: admitted {op} at {site} into {string.Join(",", scopes)}");
        }

        /// <summary>The owner's transaction began, or ended (committed, rolled back, or doomed by a close).</summary>
        public void TransactionLive(ModelOp owner, bool live, string site)
        {
            owner.TransactionLive = live;
            _sink.Trace($"{owner.Thread.Name}: transaction of {owner} {(live ? "live" : "no longer live")} at {site}");
        }

        /// <summary>Every owner transaction can no longer commit (a close or fatal stop took effect).</summary>
        public void DoomTransactions(string site)
        {
            foreach (var op in _ops.Where(o => o.TransactionLive).ToList()) this.TransactionLive(op, false, site);
        }

        public void End(ModelOp op, OpOutcome outcome, string reason, string site)
        {
            if (op.Ended) return;
            if ((op.Kind == OpKind.Owner || op.Kind == OpKind.Nested) && outcome == OpOutcome.Rejected)
            {
                _sink.Assert(!op.TransactionLive,
                    $"Safety violated: no owner rejected while its transaction is active. {op} was refused at {site} ({reason}) " +
                    "while its transaction could still commit.");
            }
            op.Outcome = outcome;
            op.Reason = reason;
            _sink.Trace($"{op.Thread.Name}: end {op} at {site}");
            _sink.OpEnded(op);
        }

        /// <summary>Operations admitted and not yet ended: the work a close may have to wait for.</summary>
        public IEnumerable<ModelOp> Active => _ops.Where(o => o.Admitted && !o.Ended && !o.IsMaintenance);

        public IEnumerable<ModelOp> Pending => _ops.Where(o => !o.Ended);
    }
}
