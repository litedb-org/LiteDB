using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Shared
{
    /// <summary>Deliberately broken variants: each one must be caught by the generic properties.</summary>
    public enum SharedMutation
    {
        None,
        /// <summary>AdmitLocked no longer refuses calls once Dispose started.</summary>
        AdmissionIgnoresDispose,
        /// <summary>Dispose waits for admitted calls of other threads without its bound.</summary>
        UnboundedDisposeDrain,
    }

    /// <summary>Which parts of the alphabet a Shared scenario may draw from.</summary>
    public sealed class SharedScenario
    {
        public OpKind[] Workers { get; set; } = { OpKind.Fresh, OpKind.Nested, OpKind.Continuation, OpKind.Owner, OpKind.CallbackDependency };

        public OpKind[] Maintenance { get; set; } = { OpKind.Close, OpKind.Rebuild, OpKind.Fatal };

        public bool ConcurrentDispose { get; set; } = true;

        /// <summary>
        /// docs/shared-mode-safety.md: "Waits that cross threads (a callback waiting for another
        /// thread's call) are not detected" — such a callback waits for a mutex its own call holds.
        /// By default a callback dependency is therefore only generated together with a Dispose,
        /// whose bounded drain ends the wait; set this to explore the documented hang itself.
        /// </summary>
        public bool CallbackDependencyWithoutDispose { get; set; }
    }

    /// <summary>The inner <c>LiteEngine</c> a Shared operation opens, reduced to its lifetime.</summary>
    internal sealed class InnerEngine
    {
        public InnerEngine(int id) => this.Id = id;

        public int Id { get; }

        public bool Closed { get; set; }

        public string Failure { get; set; }

        public override string ToString() => $"engine{this.Id}{(this.Closed ? "(closed)" : "")}";
    }

    /// <summary>
    /// Admission versus Dispose in a Shared connection (<c>SharedEngine</c>, LiteDB/Client/Shared):
    /// Call/AdmitLocked/EndAdmissions (SharedEngine.Calls.cs), OpenDatabase/CloseDatabase,
    /// explicit transactions and Dispose (SharedEngine.cs), the mutex ownership and its holder
    /// thread (<see cref="SharedMutexOwnerModel"/>). One connection, no pins, no leased snapshot
    /// readers, no peer connections; see docs/concurrency-models.md for what is not modeled.
    /// </summary>
    public sealed partial class SharedEngineModel : LifetimeModel
    {
        /// <summary>SharedEngine.DisposeCallWait.</summary>
        internal const int DisposeCallWait = 10000;

        /// <summary>SharedEngine.DisposeCheckpointWait.</summary>
        internal const int DisposeCheckpointWait = 2000;

        internal const string ObjectDisposed = "ObjectDisposedException(SharedEngine)";
        internal const string EngineDisposed = "LiteException(ENGINE_DISPOSED)";
        internal const string NoEngine = "NullReferenceException(_engine is null)";
        internal const string AbandonedTransaction = "LiteException(explicit transaction owner thread exited)";
        internal const string AlreadyInTransaction = "LiteException(ALREADY_EXISTS_TRANSACTION)";
        internal const string InjectedIo = "IOException(injected)";
        internal const string ForeignCompletion = "LiteException(complete on the BeginTrans thread)";

        private static readonly string[] Collections = { "c1", "c2" };
        private readonly SharedMutation _mutation;
        private readonly SharedScenario _scenario;
        private readonly Dictionary<int, int> _admitted = new Dictionary<int, int>();
        private int _engines;

        public SharedEngineModel(SharedMutation mutation = SharedMutation.None, SharedScenario scenario = null)
        {
            _mutation = mutation;
            _scenario = scenario ?? new SharedScenario();
        }

        internal SharedMutexOwnerModel Owner { get; private set; }

        /// <summary>_disposed (SharedEngine.cs:33).</summary>
        internal bool Disposed { get; private set; }

        internal int AdmittedCalls { get; private set; }

        internal InnerEngine Engine { get; private set; }

        internal int DatabaseUsers { get; private set; }

        internal bool TransactionRunning { get; private set; }

        internal int TransactionThreadId { get; private set; }

        /// <summary>Whether a WAL is left for Dispose's final checkpoint (LogHasContent).</summary>
        internal bool LogHasContent { get; private set; }

        public override void Build()
        {
            this.Owner = new SharedMutexOwnerModel(this.Host);
            this.LogHasContent = this.Host.ChooseBool();
            var workers = 1 + this.Host.Choose(2);
            var kinds = new List<OpKind>();
            for (var i = 0; i < workers; i++) kinds.Add(this.Pick(_scenario.Workers));
            var maintenance = new List<OpKind> { this.Pick(_scenario.Maintenance) };
            if (_scenario.ConcurrentDispose && workers == 1 && this.Host.ChooseBool()) maintenance.Add(OpKind.Close);
            if (kinds.Contains(OpKind.CallbackDependency) && !maintenance.Contains(OpKind.Close) && !_scenario.CallbackDependencyWithoutDispose)
                maintenance[0] = OpKind.Close;

            for (var i = 0; i < kinds.Count; i++) this.SpawnWorker($"W{i + 1}", kinds[i]);
            for (var i = 0; i < maintenance.Count; i++)
            {
                var kind = maintenance[i];
                this.Host.Spawn($"M{i + 1}", t => this.MaintenanceProgram(t, kind));
            }
        }

        private void SpawnWorker(string name, OpKind kind)
        {
            var collection = this.Pick(Collections);
            var scoped = this.Host.ChooseBool();
            switch (kind)
            {
                case OpKind.Fresh:
                    this.Host.Spawn(name, t => this.FreshProgram(t, collection, scoped));
                    break;
                case OpKind.Nested:
                    this.Host.Spawn(name, t => this.NestedProgram(t, collection, scoped));
                    break;
                case OpKind.Owner:
                    this.Host.Spawn(name, t => this.OwnerProgram(t, collection, scoped));
                    break;
                case OpKind.Continuation:
                    var handoff = new Handoff();
                    this.Host.Spawn(name, t => this.ReaderOpenProgram(t, handoff));
                    this.Host.Spawn(name + "-dispose", t => this.ReaderDisposeProgram(t, handoff));
                    break;
                case OpKind.CallbackDependency:
                    var dependency = new Handoff();
                    this.Host.Spawn(name, t => this.CallbackProgram(t, collection, dependency));
                    this.Host.Spawn(name + "-dependent", t => this.DependentProgram(t, this.Pick(Collections), scoped, dependency));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private int AdmittedDepth(ModelThread t) => _admitted.TryGetValue(t.Id, out var depth) ? depth : 0;

        /// <summary>
        /// SharedEngine.OpenDatabase without a pin (SharedEngine.cs:97-144): EnterOwner, reject an
        /// abandoned transaction, then under _useLock AdmitLocked, open the engine and count the user.
        /// Sets <see cref="ModelThread.Fault"/> when refused; admits <paramref name="op"/> otherwise.
        /// </summary>
        internal IEnumerable<Step> OpenDatabase(ModelThread t, ModelOp op, bool scoped)
        {
            yield return Step.At("SharedEngine.cs:110 EnterOwner (SharedEngine.Waiters.cs:293)");
            foreach (var step in this.Owner.Enter(t, scoped)) yield return step;

            yield return Step.At("SharedEngine.cs:114 RejectAbandonedTransaction");
            if (this.TransactionRunning && this.TransactionThreadId != t.Id)
            {
                // SharedEngine.cs:315-326: another thread got the mutex while a transaction runs.
                this.TransactionRunning = false;
                this.TransactionThreadId = 0;
                this.DatabaseUsers = 0;
                if (this.Engine != null) this.Engine.Closed = true;
                this.Engine = null;
                foreach (var step in this.Owner.Exit(t)) yield return step;
                t.Fault = AbandonedTransaction;
                yield break;
            }

            yield return Step.At("SharedEngine.cs:121 lock(_useLock): AdmitLocked, open engine, count user");
            if (this.Disposed && _mutation != SharedMutation.AdmissionIgnoresDispose)
            {
                foreach (var step in this.Owner.Exit(t)) yield return step; // :123-124
                t.Fault = ObjectDisposed; // SharedEngine.Calls.cs:31
                yield break;
            }
            _admitted[t.Id] = this.AdmittedDepth(t) + 1;
            this.AdmittedCalls++;
            if (!this.TransactionRunning && this.Engine == null) this.Engine = new InnerEngine(++_engines);
            this.DatabaseUsers++;
            this.Ledger.Admitted(op, "SharedEngine.Calls.cs:35 AdmitLocked", "connection");
        }

        /// <summary>SharedEngine.CloseDatabase without a pin (SharedEngine.cs:180-211).</summary>
        internal IEnumerable<Step> CloseDatabase(ModelThread t, int generation = -1)
        {
            yield return Step.At("SharedEngine.cs:191 CloseDatabase: lock(_useLock)");
            if (generation < 0 || generation == this.Owner.Generation)
            {
                if (this.DatabaseUsers > 0 && --this.DatabaseUsers == 0 && !this.TransactionRunning && this.Engine != null)
                {
                    this.Engine.Closed = true; // CloseRetainedCore
                    this.Engine = null;
                }
            }
            if (!this.TransactionRunning) this.TransactionThreadId = 0;
            yield return Step.At("SharedEngine.cs:209 _owner.Exit(generation)");
            foreach (var step in this.Owner.Exit(t, generation)) yield return step;
            t.Fault = null; // an Exit error would replace the call's own outcome; not part of this model
        }

        /// <summary>SharedEngine.EndAdmissions (SharedEngine.Calls.cs:145-156).</summary>
        internal void EndAdmissions(ModelThread t, int depth)
        {
            if (!_admitted.TryGetValue(t.Id, out var current) || current <= depth) return;
            this.AdmittedCalls -= current - depth;
            if (depth == 0) _admitted.Remove(t.Id);
            else _admitted[t.Id] = depth;
        }

        /// <summary>SharedEngine.Dispose(true) (SharedEngine.cs:442-494) without pins.</summary>
        private IEnumerable<Step> Dispose(ModelThread t, ModelOp op)
        {
            yield return Step.At("SharedEngine.cs:444 if (_disposed != 0) return");
            if (this.Disposed) { this.Complete(op, "SharedEngine.cs:444 already disposed"); yield break; }
            yield return Step.At("SharedEngine.cs:446 Interlocked.Exchange(ref _disposed, 1)");
            if (this.Disposed) { this.Complete(op, "SharedEngine.cs:446 already disposed"); yield break; }
            this.Disposed = true;
            this.Ledger.FenceAcquired("connection", "SharedEngine.cs:446");
            this.Ledger.DoomTransactions("SharedEngine.cs:446 (CompleteTransaction refuses from now on)");

            const string drain = "SharedEngine.Calls.cs:177 WaitForAdmittedCalls: Monitor.Wait(_useLock)";
            bool Drained() => this.AdmittedCalls - this.AdmittedDepth(t) <= 0;
            yield return _mutation == SharedMutation.UnboundedDisposeDrain ? Step.Wait(drain, Drained) : Step.TimedWait(drain, DisposeCallWait, Drained);

            yield return Step.At("SharedEngine.cs:471 lock(_useLock): close the engine, reset users");
            var closed = this.Engine != null;
            if (closed) this.Engine.Closed = true;
            this.Engine = null;
            this.DatabaseUsers = 0;

            yield return Step.At("SharedEngine.cs:483 _owner.ReleaseAll");
            foreach (var step in this.Owner.ReleaseAll(t)) yield return step;

            if (!closed && this.LogHasContent)
            {
                // CheckpointOnDispose (SharedEngine.Readers.cs:246-266): TryEnter for at most 2 s.
                var entered = new Ref<bool>();
                while (true)
                {
                    yield return Step.At("SharedEngine.Readers.cs:273 TryEnterForDispose: _owner.TryEnter");
                    foreach (var step in this.Owner.TryEnter(t, scoped: true, entered)) yield return step;
                    if (entered.Value) break;
                    yield return Step.TimedWait("SharedEngine.Readers.cs:275 TryEnterForDispose retries (Sleep(10)) for 2 s", DisposeCheckpointWait,
                        () => this.Owner.GatePermits > 0 && this.Owner.Released);
                    if (t.TimedOut) break;
                }
                if (entered.Value)
                {
                    yield return Step.At("SharedEngine.Readers.cs:253 CloseFinally: open and close an engine with checkpoint");
                    yield return Step.At("SharedEngine.Readers.cs:264 _owner.Exit");
                    foreach (var step in this.Owner.Exit(t)) yield return step;
                    t.Fault = null;
                }
            }

            yield return this.Owner.WaitForRelease("SharedEngine.cs:492");
            this.Complete(op, "SharedEngine.cs:494 Dispose returns");
        }

        public override string DescribeState() =>
            $"disposed={this.Disposed}, admittedCalls={this.AdmittedCalls}, engine={this.Engine?.ToString() ?? "null"}, users={this.DatabaseUsers}, " +
            $"transactionRunning={this.TransactionRunning} (thread {this.TransactionThreadId}); {this.Owner}";

        /// <summary>A request passed between two model threads.</summary>
        internal sealed class Handoff
        {
            public bool Started { get; set; }

            public bool Finished { get; set; }

            public bool Cancelled { get; set; }

            public int Generation { get; set; }

            public ModelOp Op { get; set; }
        }
    }
}
