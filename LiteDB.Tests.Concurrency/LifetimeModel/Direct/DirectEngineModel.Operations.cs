using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Direct
{
    /// <summary>An open query reader (BsonDataReader over QueryExecutor's enumerable).</summary>
    internal sealed class ReaderModel
    {
        public ModelOp Op { get; set; }

        public TxnModel Transaction { get; set; }

        /// <summary>Whether the reader created its query transaction and so releases it on dispose (QueryExecutor.cs:81-84).</summary>
        public bool OwnsTransaction { get; set; }

        /// <summary>The EngineState the reader validates against (BsonDataReader.cs:50).</summary>
        public EngineStateModel State { get; set; }
    }

    public sealed partial class DirectEngineModel
    {
        private IEnumerable<Step> FreshProgram(ModelThread t, string collection)
        {
            var op = this.Ledger.Begin(t, OpKind.Fresh, $"Insert into {collection}");
            foreach (var step in this.AutoWrite(t, op, collection, null, injectFailure: false)) yield return step;
            if (!op.Ended) this.Complete(op, "Transaction.cs:329 returns");
        }

        private IEnumerable<Step> NestedProgram(ModelThread t, string collection)
        {
            var reader = new Ref<ReaderModel>();
            var outer = this.Ledger.Begin(t, OpKind.Fresh, "Query reader kept open around nested work");
            foreach (var step in this.OpenReader(t, outer, reader)) yield return step;
            if (outer.Ended) yield break;

            var nested = this.Ledger.Begin(t, OpKind.Nested, $"Insert into {collection} while this thread's reader is open");
            if (reader.Value.State == this.State && this.State.Valid) this.Ledger.TransactionLive(nested, true, "reader open on this thread");
            foreach (var step in this.AutoWrite(t, nested, collection, null, injectFailure: false)) yield return step;
            if (!nested.Ended) this.Complete(nested, "Transaction.cs:329 returns");
            this.Ledger.TransactionLive(nested, false, "nested work returned");

            foreach (var step in this.DisposeReader(t, reader.Value)) yield return step;
        }

        private IEnumerable<Step> OwnerProgram(ModelThread t, string collection)
        {
            var op = this.Ledger.Begin(t, OpKind.Owner, $"BeginTrans, Insert into {collection}, Commit");
            yield return Step.At("Transaction.cs:245 BeginTrans: _state.Validate()");
            if (!this.State.Valid) { this.Reject(op, this.State.Failure ?? Faults.EngineDisposed, "Transaction.cs:245"); yield break; }

            var monitor = this.Monitor;
            var transaction = new Ref<TxnModel>();
            var isNew = new Ref<bool>();
            yield return Step.At("Transaction.cs:249 TransactionMonitor.GetTransaction");
            foreach (var step in monitor.GetTransaction(t, false, PragmaTimeout, transaction, isNew)) yield return step;
            if (t.Fault != null) { this.Reject(op, t.Fault, "Transaction.cs:249"); t.Fault = null; yield break; }
            transaction.Value.Explicit = true;
            this.Ledger.Admitted(op, "Transaction.cs:253 explicit transaction begun", this.Scopes(monitor));
            if (this.State.Valid && !monitor.Disposed) this.Ledger.TransactionLive(op, true, "Transaction.cs:253");

            foreach (var step in this.AutoWrite(t, op, collection, null, injectFailure: false)) yield return step;
            if (op.Ended) yield break;

            yield return Step.At("Transaction.cs:267 Commit: _state.Validate()");
            if (!this.State.Valid) { this.Reject(op, this.State.Failure ?? Faults.EngineDisposed, "Transaction.cs:267"); yield break; }
            var current = this.Monitor;
            yield return Step.At("TransactionCompletionGuard.cs:396 GetTransaction(create: false)");
            if (current.Disposed) { this.Reject(op, Faults.MonitorDisposed, "TransactionMonitor.cs:52"); yield break; }
            if (!current.Slot.TryGetValue(t.Id, out var own))
            {
                // A failed write already rolled the transaction back: Commit returns false.
                this.Ledger.TransactionLive(op, false, "Transaction.cs:284 nothing to commit");
                this.Complete(op, "Transaction.cs:284 Commit returns false");
                yield break;
            }
            foreach (var step in this.CommitAndRelease(t, own, injectFailure: false)) yield return step;
            this.Ledger.TransactionLive(op, false, "Transaction.cs:278 committed or failed");
            if (t.Fault != null) { this.Fail(op, t.Fault, "Transaction.cs:278"); t.Fault = null; yield break; }
            this.Complete(op, "Transaction.cs:280 Commit returns true");
        }

        private IEnumerable<Step> ReaderOpenProgram(ModelThread t, Handoff<ReaderModel> handoff, bool sameThread)
        {
            var op = this.Ledger.Begin(t, OpKind.Continuation,
                sameThread ? "Query reader read and disposed later on this thread" : "Query reader handed to another thread");
            var reader = new Ref<ReaderModel>();
            foreach (var step in this.OpenReader(t, op, reader)) yield return step;
            if (op.Ended) { handoff.Cancelled = true; yield break; }
            if (sameThread)
            {
                foreach (var step in this.DisposeReader(t, reader.Value)) yield return step;
                yield break;
            }
            handoff.Value = reader.Value;
            handoff.Started = true;
        }

        private IEnumerable<Step> ReaderDisposeProgram(ModelThread t, Handoff<ReaderModel> handoff)
        {
            yield return Step.Wait("user code: wait for the reader handed over", () => handoff.Started || handoff.Cancelled);
            if (handoff.Cancelled) yield break;
            foreach (var step in this.DisposeReader(t, handoff.Value)) yield return step;
        }

        private IEnumerable<Step> CallbackProgram(ModelThread t, string collection, Handoff<bool> dependency)
        {
            var op = this.Ledger.Begin(t, OpKind.CallbackDependency, $"Insert into {collection} whose lazy input waits for work on another thread");
            foreach (var step in this.AutoWrite(t, op, collection, () => this.Callback(dependency), injectFailure: false)) yield return step;
            if (!dependency.Started) dependency.Cancelled = true;
            if (!op.Ended) this.Complete(op, "Transaction.cs:329 returns");
        }

        private IEnumerable<Step> Callback(Handoff<bool> dependency)
        {
            yield return Step.At("user callback (Insert's lazy input): start fresh work on another thread");
            dependency.Started = true;
            yield return Step.Wait("user callback: blocks until the dependent operation returns (Task.Wait)", () => dependency.Finished);
        }

        private IEnumerable<Step> DependentProgram(ModelThread t, string collection, Handoff<bool> dependency)
        {
            yield return Step.Wait("dependent thread: waits for the callback's request", () => dependency.Started || dependency.Cancelled);
            if (dependency.Cancelled) yield break;
            var op = this.Ledger.Begin(t, OpKind.Fresh, $"Insert into {collection} requested by another operation's callback");
            foreach (var step in this.AutoWrite(t, op, collection, null, injectFailure: false)) yield return step;
            if (!op.Ended) this.Complete(op, "Transaction.cs:329 returns");
            dependency.Finished = true;
        }

        /// <summary>
        /// LiteEngine.ExecuteAutoTransaction (Transaction.cs:313-342) for an Insert: validate, get or
        /// join the thread transaction, take the collection's write lock (Snapshot), run the input
        /// <paramref name="callback"/>, and commit a transaction it created. Ends <paramref name="op"/> on
        /// any exception: rejected before admission, failed after it.
        /// </summary>
        internal IEnumerable<Step> AutoWrite(ModelThread t, ModelOp op, string collection, Func<IEnumerable<Step>> callback, bool injectFailure)
        {
            yield return Step.At("Transaction.cs:315 ExecuteAutoTransaction: _state.Validate()");
            if (!this.State.Valid) { this.Reject(op, this.State.Failure ?? Faults.EngineDisposed, "Transaction.cs:315"); yield break; }

            var monitor = this.Monitor;
            var transaction = new Ref<TxnModel>();
            var isNew = new Ref<bool>();
            yield return Step.At("Transaction.cs:319 TransactionMonitor.GetTransaction");
            foreach (var step in monitor.GetTransaction(t, false, PragmaTimeout, transaction, isNew)) yield return step;
            if (t.Fault != null) { this.Reject(op, t.Fault, "TransactionMonitor.GetTransaction"); t.Fault = null; yield break; }
            this.Ledger.Admitted(op, isNew.Value ? "TransactionMonitor.cs:77 transaction registered" : "TransactionMonitor.cs:53 joined the thread transaction",
                this.Scopes(monitor));

            yield return Step.At($"Snapshot(write {collection}): LockService.EnterLock");
            foreach (var step in monitor.Locker.EnterLock(t, collection, PragmaTimeout)) yield return step;
            if (t.Fault == null)
            {
                transaction.Value.LockedCollection = transaction.Value.LockedCollection ?? collection;
                if (callback != null)
                {
                    foreach (var step in callback()) yield return step;
                    // Insert.cs:32 validates the engine before each document the input yields.
                    if (!this.State.Valid) t.Fault = this.State.Failure ?? Faults.EngineDisposed;
                }
            }
            if (t.Fault == null && isNew.Value)
            {
                foreach (var step in this.CommitAndRelease(t, transaction.Value, injectFailure)) yield return step;
            }
            if (t.Fault == null) yield break;

            // Transaction.cs:331-341: a non-fatal error rolls an active transaction back and rethrows.
            var fault = t.Fault;
            t.Fault = null;
            if (fault != Faults.InjectedIo && transaction.Value.State == TxnState.Active)
            {
                foreach (var step in this.RollbackAndRelease(t, transaction.Value)) yield return step;
                t.Fault = null;
                if (transaction.Value.Explicit) this.Ledger.TransactionLive(op, false, "Transaction.cs:337 explicit transaction aborted");
            }
            this.Fail(op, fault, "Transaction.cs:340 rethrows");
        }

        /// <summary>LiteEngine.CommitAndReleaseTransaction (Transaction.cs:344-365).</summary>
        private IEnumerable<Step> CommitAndRelease(ModelThread t, TxnModel transaction, bool injectFailure)
        {
            yield return Step.At("Transaction.cs:348 TransactionService.Commit");
            // TransactionService.cs:290 ENSURE(Active): a transaction disposed by close cannot commit.
            var fault = transaction.State != TxnState.Active ? Faults.TransactionDisposed : injectFailure ? Faults.InjectedIo : null;
            if (fault == null)
            {
                transaction.State = TxnState.Committed;
                if (transaction.LockedCollection != null) transaction.Monitor.Locker.ExitLock(t, transaction.LockedCollection);
                yield return Step.At("Transaction.cs:349 TransactionMonitor.ReleaseTransaction");
                transaction.Monitor.ReleaseTransaction(t, transaction);
                yield break;
            }
            // Transaction.cs:351-356: completion failed, publish it as fatal and rethrow.
            foreach (var step in this.Lifecycle.Stop(t, fault)) yield return step;
            t.Fault = fault;
        }

        /// <summary>LiteEngine.RollbackAndReleaseTransaction (Transaction.cs:367-379), for an active transaction.</summary>
        private IEnumerable<Step> RollbackAndRelease(ModelThread t, TxnModel transaction)
        {
            yield return Step.At("Transaction.cs:371 TransactionService.Rollback");
            transaction.State = TxnState.Aborted;
            if (transaction.LockedCollection != null) transaction.Monitor.Locker.ExitLock(t, transaction.LockedCollection);
            yield return Step.At("Transaction.cs:372 TransactionMonitor.ReleaseTransaction");
            transaction.Monitor.ReleaseTransaction(t, transaction);
        }

        /// <summary>LiteEngine.Query (Query.cs:15-47) and QueryExecutor.ExecuteQuery (QueryExecutor.cs:70-87).</summary>
        private IEnumerable<Step> OpenReader(ModelThread t, ModelOp op, Ref<ReaderModel> reader)
        {
            yield return Step.At("Query.cs:19 LiteEngine.Query: _state.Validate()");
            if (!this.State.Valid) { this.Reject(op, this.State.Failure ?? Faults.EngineDisposed, "Query.cs:19"); yield break; }
            var state = this.State;
            var monitor = this.Monitor;
            var transaction = new Ref<TxnModel>();
            var isNew = new Ref<bool>();
            yield return Step.At("QueryExecutor.cs:73 GetTransaction(create, queryOnly: true)");
            foreach (var step in monitor.GetTransaction(t, true, PragmaTimeout, transaction, isNew)) yield return step;
            if (t.Fault != null) { this.Reject(op, t.Fault, "QueryExecutor.cs:73"); t.Fault = null; yield break; }
            this.Ledger.Admitted(op, "QueryExecutor.cs:73 reader transaction", this.Scopes(monitor));
            reader.Value = new ReaderModel { Op = op, Transaction = transaction.Value, OwnsTransaction = isNew.Value, State = state };
        }

        /// <summary>BsonDataReader.Read (BsonDataReader.cs:107) then Dispose, releasing the reader's lease (QueryExecutor.cs:83).</summary>
        private IEnumerable<Step> DisposeReader(ModelThread t, ReaderModel reader)
        {
            yield return Step.At("BsonDataReader.cs:107 Read: _state.Validate()");
            var readFailed = !reader.State.Valid;
            yield return Step.At("QueryExecutor.cs:83 reader Dispose: TransactionMonitor.ReleaseTransaction");
            if (reader.OwnsTransaction) reader.Transaction.Monitor.ReleaseTransaction(t, reader.Transaction);
            if (readFailed) this.Fail(reader.Op, reader.State.Failure ?? Faults.EngineDisposed, "BsonDataReader.cs:107");
            else this.Complete(reader.Op, "reader disposed");
        }
    }
}
