using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Direct
{
    /// <summary>Deliberately broken variants: each one must be caught by the generic properties.</summary>
    public enum DirectMutation
    {
        None,
        /// <summary>TransactionRegistry.Add publishes without re-checking close, and GetTransaction skips its last disposed check.</summary>
        UnfencedRegistration,
        /// <summary>TransactionGate blocks a thread's further leases behind a waiting writer (strict writer preference).</summary>
        NestedLeaseNotExempt,
        /// <summary>TransactionGate waits (transaction admission and exclusive) ignore the pragma timeout.</summary>
        UnboundedGateWaits,
    }

    /// <summary>Which parts of the alphabet a scenario may draw from.</summary>
    public sealed class DirectScenario
    {
        public OpKind[] Workers { get; set; } = { OpKind.Fresh, OpKind.Nested, OpKind.Continuation, OpKind.Owner, OpKind.CallbackDependency };

        public OpKind[] Maintenance { get; set; } = { OpKind.Rebuild, OpKind.Close, OpKind.Fatal };

        /// <summary>Allow a second maintenance thread: the user's Dispose racing the first maintenance operation.</summary>
        public bool ConcurrentDispose { get; set; } = true;
    }

    /// <summary><c>EngineState</c> (LiteDB/Engine/EngineState.cs): disposed flag and published fatal failure.</summary>
    internal sealed class EngineStateModel
    {
        public bool Disposed { get; set; }

        public string Failure { get; set; }

        /// <summary>EngineState.Validate (EngineState.cs:375-380).</summary>
        public bool Valid => this.Failure == null && !this.Disposed;

        public override string ToString() => $"state(disposed={this.Disposed}{(this.Failure == null ? "" : ", failure=" + this.Failure)})";
    }

    /// <summary>
    /// Admission versus maintenance in the Direct engine (<c>LiteEngine</c>): per-thread
    /// transactions admitted through <c>TransactionMonitor</c>/<c>LockService</c>, and close
    /// (Dispose), rebuild and fatal stop. Scenario per iteration: one or two workers, each running
    /// one operation kind (helper threads for cross-thread continuation and callback dependencies),
    /// and one or two maintenance threads. See docs/concurrency-models.md for the traceability table.
    /// </summary>
    public sealed partial class DirectEngineModel : LifetimeModel
    {
        /// <summary>Default pragma TIMEOUT (EnginePragmas: 1 minute).</summary>
        internal const int PragmaTimeout = 60000;

        /// <summary>WalIndexService.READER_WAIT_MILLISECONDS, the close checkpoint's wait for leases.</summary>
        internal const int ReaderWait = 10;

        private static readonly string[] Collections = { "c1", "c2" };

        private readonly DirectMutation _mutation;
        private readonly DirectScenario _scenario;
        private int _generations;
        private int _transactions;

        public DirectEngineModel(DirectMutation mutation = DirectMutation.None, DirectScenario scenario = null)
        {
            _mutation = mutation;
            _scenario = scenario ?? new DirectScenario();
            this.Lifecycle = new DirectLifecycle(this);
        }

        internal EngineStateModel State { get; set; }

        internal LockServiceModel Locker { get; set; }

        internal TransactionMonitorModel Monitor { get; set; }

        internal DirectMutation Mutation => _mutation;

        internal DirectLifecycle Lifecycle { get; }

        /// <summary>Whether the WAL has content at close (decides whether close checkpoints).</summary>
        internal bool LogHasContent { get; private set; }

        internal new LifetimeLedger Ledger => base.Ledger;

        internal new IModelHost Host => base.Host;

        public override void Build()
        {
            this.Open();
            this.LogHasContent = this.Host.ChooseBool();
            var workers = 1 + this.Host.Choose(2);
            var maintenance = workers == 2 || !_scenario.ConcurrentDispose ? 1 : 1 + this.Host.Choose(2);
            for (var i = 1; i <= workers; i++) this.SpawnWorker($"W{i}", this.Pick(_scenario.Workers));
            for (var i = 1; i <= maintenance; i++)
            {
                // A second maintenance thread is the user's Dispose racing the first one.
                var kind = i == 1 ? this.Pick(_scenario.Maintenance) : OpKind.Close;
                this.Host.Spawn($"M{i}", t => this.Lifecycle.Run(t, kind));
            }
        }

        /// <summary>LiteEngine.Open (LiteEngine.cs:84-194): a new state, lock service and monitor.</summary>
        internal void Open()
        {
            this.State = new EngineStateModel();
            this.OpenLocker();
            this.OpenMonitor();
        }

        internal void OpenLocker() => this.Locker = new LockServiceModel(++_generations, _mutation);

        internal void OpenMonitor() => this.Monitor = new TransactionMonitorModel(this.Locker, _mutation, () => ++_transactions);

        internal string[] Scopes(TransactionMonitorModel monitor) => new[] { "connection", $"gen{monitor.Generation}" };

        private void SpawnWorker(string name, OpKind kind)
        {
            var collection = this.Pick(Collections);
            switch (kind)
            {
                case OpKind.Fresh:
                    this.Host.Spawn(name, t => this.FreshProgram(t, collection));
                    break;
                case OpKind.Nested:
                    this.Host.Spawn(name, t => this.NestedProgram(t, collection));
                    break;
                case OpKind.Owner:
                    this.Host.Spawn(name, t => this.OwnerProgram(t, collection));
                    break;
                case OpKind.Continuation:
                    var handoff = new Handoff<ReaderModel>();
                    var sameThread = this.Host.ChooseBool();
                    this.Host.Spawn(name, t => this.ReaderOpenProgram(t, handoff, sameThread));
                    if (!sameThread) this.Host.Spawn(name + "-dispose", t => this.ReaderDisposeProgram(t, handoff));
                    break;
                case OpKind.CallbackDependency:
                    var dependency = new Handoff<bool>();
                    var dependentCollection = this.Pick(Collections);
                    this.Host.Spawn(name, t => this.CallbackProgram(t, collection, dependency));
                    this.Host.Spawn(name + "-dependent", t => this.DependentProgram(t, dependentCollection, dependency));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public override string DescribeState() => $"{this.State}; {this.Locker}; {this.Monitor}";

        /// <summary>A value passed between two model threads (a reader handed over, a request and its completion).</summary>
        internal sealed class Handoff<T>
        {
            public bool Started { get; set; }

            public bool Finished { get; set; }

            public bool Cancelled { get; set; }

            public T Value { get; set; }
        }
    }
}
