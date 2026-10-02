using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>What a model can ask of the explorer while it builds or runs a scenario.</summary>
    public interface IModelHost
    {
        LifetimeLedger Ledger { get; }

        /// <summary>
        /// Start an abstract thread. A <paramref name="daemon"/> thread (a holder or service
        /// thread of the real code) does not keep a finished scenario alive.
        /// </summary>
        ModelThread Spawn(string name, Func<ModelThread, IEnumerable<Step>> program, bool daemon = false);

        /// <summary>A controlled nondeterministic choice in [0, count); recorded in the Coyote trace.</summary>
        int Choose(int count);

        bool ChooseBool();

        void Trace(string message);

        void Assert(bool condition, string message);
    }

    /// <summary>
    /// A small model of one real lifetime state machine. <see cref="Build"/> picks a scenario
    /// (threads, operation kinds, maintenance kinds) with <see cref="IModelHost.Choose"/> and
    /// spawns the threads; their programs mirror the real code step by step and report to
    /// <see cref="LifetimeLedger"/>, which owns the generic properties.
    /// </summary>
    public abstract class LifetimeModel
    {
        protected IModelHost Host { get; private set; }

        protected LifetimeLedger Ledger => this.Host.Ledger;

        internal void Attach(IModelHost host) => this.Host = host;

        /// <summary>Choose and spawn the scenario of one iteration.</summary>
        public abstract void Build();

        /// <summary>A one-line dump of the model state, appended to failure reports.</summary>
        public virtual string DescribeState() => string.Empty;

        /// <summary>Pick one element of <paramref name="items"/> with a controlled choice.</summary>
        protected T Pick<T>(IReadOnlyList<T> items) => items[this.Host.Choose(items.Count)];

        /// <summary>
        /// Ends <paramref name="op"/> as rejected at <paramref name="site"/>. Helper for
        /// iterator programs, which cannot yield from inside try/catch.
        /// </summary>
        protected void Reject(ModelOp op, string reason, string site) => this.Ledger.End(op, OpOutcome.Rejected, reason, site);

        protected void Fail(ModelOp op, string reason, string site) => this.Ledger.End(op, OpOutcome.Failed, reason, site);

        protected void Complete(ModelOp op, string site) => this.Ledger.End(op, OpOutcome.Completed, null, site);
    }
}
