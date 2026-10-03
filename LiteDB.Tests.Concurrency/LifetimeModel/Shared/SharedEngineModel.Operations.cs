using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Shared
{
    public sealed partial class SharedEngineModel
    {
        private IEnumerable<Step> FreshProgram(ModelThread t, string collection, bool scoped)
        {
            var op = this.Ledger.Begin(t, OpKind.Fresh, $"Insert into {collection}{(scoped ? " (array: scoped ownership)" : "")}");
            return this.Write(t, op, collection, scoped, null, fatal: false, endOp: true);
        }

        private IEnumerable<Step> NestedProgram(ModelThread t, string collection, bool scoped)
        {
            // A lazy input sequence is never scoped (SharedEngine.cs:376 scoped: docs is BsonDocument[]).
            var outer = this.Ledger.Begin(t, OpKind.Fresh, $"Insert into {collection} whose lazy input calls this connection on the same thread");
            return this.Write(t, outer, collection, scoped: false, () => this.NestedCallback(t, collection, scoped), fatal: false, endOp: true);
        }

        private IEnumerable<Step> NestedCallback(ModelThread t, string collection, bool scoped)
        {
            var nested = this.Ledger.Begin(t, OpKind.Nested, $"Insert into {collection} from the callback (same-thread recursion)");
            if (!this.Disposed) this.Ledger.TransactionLive(nested, true, "outer call admitted on this thread");
            foreach (var step in this.Write(t, nested, collection, scoped, null, fatal: false, endOp: true)) yield return step;
            this.Ledger.TransactionLive(nested, false, "nested call returned");
        }

        private IEnumerable<Step> CallbackProgram(ModelThread t, string collection, Handoff dependency)
        {
            var op = this.Ledger.Begin(t, OpKind.CallbackDependency, $"Insert into {collection} whose lazy input waits for a call on another thread");
            foreach (var step in this.Write(t, op, collection, scoped: false, () => this.Callback(dependency), fatal: false, endOp: true)) yield return step;
            if (!dependency.Started) dependency.Cancelled = true;
        }

        private IEnumerable<Step> Callback(Handoff dependency)
        {
            yield return Step.At("user callback (Insert's lazy input): start a call on another thread");
            dependency.Started = true;
            yield return Step.Wait("user callback: blocks until the other thread's call returns (Task.Wait)", () => dependency.Finished);
        }

        private IEnumerable<Step> DependentProgram(ModelThread t, string collection, bool scoped, Handoff dependency)
        {
            yield return Step.Wait("dependent thread: waits for the callback's request", () => dependency.Started || dependency.Cancelled);
            if (dependency.Cancelled) yield break;
            var op = this.Ledger.Begin(t, OpKind.Fresh, $"Insert into {collection} requested by another call's callback");
            foreach (var step in this.Write(t, op, collection, scoped, null, fatal: false, endOp: true)) yield return step;
            dependency.Finished = true;
        }

        private IEnumerable<Step> MaintenanceProgram(ModelThread t, OpKind kind)
        {
            switch (kind)
            {
                case OpKind.Close:
                    return this.Dispose(t, this.Ledger.Begin(t, OpKind.Close, "SharedEngine.Dispose"));
                case OpKind.Rebuild:
                    // SharedEngine.Rebuild (SharedEngine.cs:352-372): the inner engine rebuilds under the mutex.
                    return this.Write(t, this.Ledger.Begin(t, OpKind.Rebuild, "SharedEngine.Rebuild"), "c1", false, null, fatal: false, endOp: true);
                case OpKind.Fatal:
                    return this.Write(t, this.Ledger.Begin(t, OpKind.Fatal, "Insert whose inner commit fails with an I/O error"), "c1", false, null, fatal: true, endOp: true);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>
        /// A public write: SharedEngine.Call (SharedEngine.Calls.cs:61-75) around WriteDatabase
        /// without a pin (SharedEngine.Readers.cs:73-88). The inner engine call runs
        /// <paramref name="callback"/> (a lazy input sequence) while the connection holds the mutex.
        /// </summary>
        private IEnumerable<Step> Write(ModelThread t, ModelOp op, string collection, bool scoped, Func<IEnumerable<Step>> callback, bool fatal, bool endOp)
        {
            yield return Step.At("SharedEngine.Calls.cs:64 Call: AdmittedDepth");
            var depth = this.AdmittedDepth(t);
            foreach (var step in this.OpenDatabase(t, op, scoped)) yield return step;
            if (t.Fault != null)
            {
                var refused = t.Fault;
                t.Fault = null;
                this.EndAdmissions(t, depth);
                this.Reject(op, refused, "SharedEngine.OpenDatabase");
                yield break;
            }

            yield return Step.At($"SharedEngine.cs:376 _engine.Insert({collection}) on the inner engine");
            string fault = null;
            var engine = this.Engine;
            if (engine == null) fault = NoEngine;
            else if (engine.Closed) fault = engine.Failure ?? EngineDisposed;
            else
            {
                if (callback != null) foreach (var step in callback()) yield return step;
                yield return Step.At("inner engine: commit the operation (or join the explicit transaction)");
                if (engine.Closed) fault = engine.Failure ?? EngineDisposed; // closed under the call
                else if (fatal)
                {
                    engine.Failure = InjectedIo; // EngineState.Stop closes the inner engine
                    engine.Closed = true;
                    fault = InjectedIo;
                }
            }

            foreach (var step in this.CloseDatabase(t)) yield return step;
            yield return Step.At("SharedEngine.Calls.cs:73 EndAdmissions");
            this.EndAdmissions(t, depth);
            if (fault != null) this.Fail(op, fault, "SharedEngine.cs:376");
            else if (endOp) this.Complete(op, "SharedEngine.Calls.cs:68 call returns");
        }

        private IEnumerable<Step> OwnerProgram(ModelThread t, string collection, bool scoped)
        {
            var op = this.Ledger.Begin(t, OpKind.Owner, $"BeginTrans, Insert into {collection}, Commit (Rollback after a failure)");

            // BeginTrans: Call(BeginTransCore) (SharedEngine.cs:236-263).
            yield return Step.At("SharedEngine.Calls.cs:64 Call(BeginTransCore): AdmittedDepth");
            var depth = this.AdmittedDepth(t);
            foreach (var step in this.OpenDatabase(t, op, scoped: false)) yield return step;
            if (t.Fault != null)
            {
                this.EndAdmissions(t, depth);
                this.Reject(op, t.Fault, "SharedEngine.cs:240");
                t.Fault = null;
                yield break;
            }
            yield return Step.At("SharedEngine.cs:244 _engine.BeginTrans");
            if (this.Engine == null || this.Engine.Closed)
            {
                var fault = this.Engine == null ? NoEngine : this.Engine.Failure ?? EngineDisposed;
                foreach (var step in this.CloseDatabase(t)) yield return step; // :258-261
                this.EndAdmissions(t, depth);
                this.Fail(op, fault, "SharedEngine.cs:244");
                yield break;
            }
            this.TransactionThreadId = t.Id; // :247-251, keeping the ownership recursion
            this.TransactionRunning = true;
            if (!this.Disposed) this.Ledger.TransactionLive(op, true, "SharedEngine.cs:248");
            yield return Step.At("SharedEngine.Calls.cs:73 EndAdmissions");
            this.EndAdmissions(t, depth);

            foreach (var step in this.Write(t, op, collection, scoped, null, fatal: false, endOp: false)) yield return step;
            var commit = !op.Ended;
            var result = new Ref<string>();
            foreach (var step in this.CompleteTransaction(t, op, commit, result)) yield return step;
            if (result.Value == null) this.Complete(op, $"SharedEngine.cs:{(commit ? 265 : 267)} completion returns");
            else if (result.Value == ObjectDisposed || result.Value == ForeignCompletion) this.Reject(op, result.Value, "SharedEngine.cs:276-290");
            else this.Fail(op, result.Value, "SharedEngine.cs:294");
            this.Ledger.TransactionLive(op, false, "transaction completed");
        }

        /// <summary>
        /// Call(CompleteTransaction) (SharedEngine.cs:269-308) without a pin. Sets
        /// <paramref name="fault"/> to the exception the call throws, null when it returns.
        /// </summary>
        private IEnumerable<Step> CompleteTransaction(ModelThread t, ModelOp op, bool commit, Ref<string> fault)
        {
            yield return Step.At("SharedEngine.Calls.cs:64 Call(CompleteTransaction): AdmittedDepth");
            var depth = this.AdmittedDepth(t);
            yield return Step.At("SharedEngine.cs:276 _owner.TryEnter");
            var entered = new Ref<bool>();
            foreach (var step in this.Owner.TryEnter(t, scoped: false, entered)) yield return step;
            if (!entered.Value)
            {
                // :279-280 rolling back nothing is safe; a commit from another thread throws.
                if (this.TransactionRunning && commit) fault.Value = ForeignCompletion;
                this.EndAdmissions(t, depth);
                yield break;
            }

            yield return Step.At("SharedEngine.cs:287 lock(_useLock): AdmitLocked");
            var complete = false;
            if (!(!commit && this.Disposed))
            {
                if (this.Disposed && _mutation != SharedMutation.AdmissionIgnoresDispose) fault.Value = ObjectDisposed;
                else
                {
                    _admitted[t.Id] = this.AdmittedDepth(t) + 1;
                    this.AdmittedCalls++;
                    complete = this.TransactionRunning && this.Engine != null; // :293
                }
            }
            if (complete)
            {
                yield return Step.At($"SharedEngine.cs:294 _engine.{(commit ? "Commit" : "Rollback")}");
                if (this.Engine.Closed) fault.Value = this.Engine.Failure ?? EngineDisposed;
                this.TransactionRunning = false; // finally :297-300
                foreach (var step in this.CloseDatabase(t)) yield return step;
            }
            yield return Step.At("SharedEngine.cs:306 _owner.Exit");
            foreach (var step in this.Owner.Exit(t)) yield return step;
            t.Fault = null;
            yield return Step.At("SharedEngine.Calls.cs:73 EndAdmissions");
            this.EndAdmissions(t, depth);
        }

        private IEnumerable<Step> ReaderOpenProgram(ModelThread t, Handoff handoff)
        {
            var op = this.Ledger.Begin(t, OpKind.Continuation, "FOR UPDATE query streamed under the mutex, disposed on another thread");
            handoff.Op = op;
            yield return Step.At("SharedEngine.Calls.cs:64 Call(QueryCore): AdmittedDepth");
            var depth = this.AdmittedDepth(t);
            // QueryCore: a write query (reads == false) opens like a write (SharedEngine.Query.cs:73).
            foreach (var step in this.OpenDatabase(t, op, scoped: false)) yield return step;
            if (t.Fault != null)
            {
                this.EndAdmissions(t, depth);
                this.Reject(op, t.Fault, "SharedEngine.Query.cs:73");
                t.Fault = null;
                handoff.Cancelled = true;
                yield break;
            }
            yield return Step.At("SharedEngine.Query.cs:172 QueryUnderMutex: _engine.Query");
            if (this.Engine == null || this.Engine.Closed)
            {
                var fault = this.Engine == null ? NoEngine : this.Engine.Failure ?? EngineDisposed;
                foreach (var step in this.CloseDatabase(t)) yield return step;
                this.EndAdmissions(t, depth);
                this.Fail(op, fault, "SharedEngine.Query.cs:172");
                handoff.Cancelled = true;
                yield break;
            }
            handoff.Generation = this.Owner.Generation; // :176 the reader keeps this ownership
            yield return Step.At("SharedEngine.Calls.cs:73 EndAdmissions (the reader retains the mutex)");
            this.EndAdmissions(t, depth);
            handoff.Started = true;
        }

        private IEnumerable<Step> ReaderDisposeProgram(ModelThread t, Handoff handoff)
        {
            yield return Step.Wait("user code: wait for the reader handed over", () => handoff.Started || handoff.Cancelled);
            if (handoff.Cancelled) yield break;
            yield return Step.At("SharedDataReader.Dispose: CloseDatabase(use: null, hold: true, generation)");
            foreach (var step in this.CloseDatabase(t, handoff.Generation)) yield return step;
            this.Complete(handoff.Op, "reader disposed");
        }
    }
}
