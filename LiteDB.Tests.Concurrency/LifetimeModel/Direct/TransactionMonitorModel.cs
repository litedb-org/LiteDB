using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Direct
{
    internal enum TxnState
    {
        Active,
        Committed,
        Aborted,
        Disposed,
    }

    /// <summary>A <c>TransactionService</c> (LiteDB/Engine/Services/TransactionService.cs), reduced to its lifetime.</summary>
    internal sealed class TxnModel
    {
        public TxnModel(int id, int owner, bool queryOnly, TransactionMonitorModel monitor)
        {
            this.Id = id;
            this.Owner = owner;
            this.QueryOnly = queryOnly;
            this.Monitor = monitor;
        }

        public int Id { get; }

        /// <summary>TransactionService.OwnerThread: the thread whose lease this transaction holds.</summary>
        public int Owner { get; }

        public bool QueryOnly { get; }

        public bool Explicit { get; set; }

        public TxnState State { get; set; } = TxnState.Active;

        /// <summary>The collection whose write lock a snapshot of this transaction holds (null when none).</summary>
        public string LockedCollection { get; set; }

        public TransactionMonitorModel Monitor { get; }

        public override string ToString() => $"txn{this.Id}(owner T{this.Owner}{(this.QueryOnly ? ", query" : "")}, {this.State})";
    }

    /// <summary>
    /// One <c>TransactionMonitor</c> (LiteDB/Engine/Services/TransactionMonitor.cs) with its
    /// <c>TransactionRegistry</c> (TransactionRegistry.cs) and per-thread slot. Its lock service
    /// is the instance it was created with (TransactionMonitor.cs:20, used for every release).
    /// </summary>
    internal sealed class TransactionMonitorModel
    {
        private readonly DirectMutation _mutation;
        private readonly System.Func<int> _nextId;

        public TransactionMonitorModel(LockServiceModel locker, DirectMutation mutation, System.Func<int> nextId)
        {
            this.Locker = locker;
            _mutation = mutation;
            _nextId = nextId;
        }

        public LockServiceModel Locker { get; }

        public int Generation => this.Locker.Generation;

        /// <summary>TransactionMonitor._disposed (TransactionMonitor.cs:25).</summary>
        public bool Disposed { get; private set; }

        /// <summary>TransactionRegistry._closed (TransactionRegistry.cs:17).</summary>
        public bool RegistryClosed { get; private set; }

        public HashSet<TxnModel> Registry { get; } = new HashSet<TxnModel>();

        /// <summary>The ThreadLocal slot of writable transactions (TransactionMonitor.cs:16).</summary>
        public Dictionary<int, TxnModel> Slot { get; } = new Dictionary<int, TxnModel>();

        /// <summary>
        /// TransactionMonitor.GetTransaction(create: true) (TransactionMonitor.cs:50-99), including
        /// TransactionRegistry.Add (TransactionRegistry.cs:19-49). Sets <paramref name="result"/>.
        /// </summary>
        public IEnumerable<Step> GetTransaction(ModelThread t, bool queryOnly, int timeout, Ref<TxnModel> result, Ref<bool> isNew)
        {
            if (this.Disposed) { t.Fault = Faults.MonitorDisposed; yield break; } // :52
            if (this.Slot.TryGetValue(t.Id, out var existing))
            {
                // :53, :93-96 the thread's transaction is joined, no new lease.
                result.Value = existing;
                isNew.Value = false;
                yield break;
            }

            yield return Step.At("TransactionMonitor.cs:62 ThrowIfDisposed before the lease");
            if (this.Disposed) { t.Fault = Faults.MonitorDisposed; yield break; }

            yield return Step.At("TransactionMonitor.cs:70 LockService.EnterTransaction");
            foreach (var step in this.Locker.EnterTransaction(t, timeout)) yield return step;
            if (t.Fault != null) yield break; // :79-90 nothing entered yet

            yield return Step.At("TransactionMonitor.cs:72 ThrowIfDisposed after the lease");
            if (this.Disposed) { this.Unwind(t, null, Faults.MonitorDisposed); yield break; }
            var transaction = new TxnModel(_nextId(), t.Id, queryOnly, this);

            // TransactionRegistry.Add: :25 refuses a closed registry before reserving capacity.
            if (this.RegistryClosed) { this.Unwind(t, transaction, Faults.RegistryClosed); yield break; }
            yield return Step.At("TransactionRegistry.cs:36 publish into a slot");
            this.Registry.Add(transaction);
            if (_mutation != DirectMutation.UnfencedRegistration)
            {
                yield return Step.At("TransactionRegistry.cs:40 re-check closed after publication");
                if (this.RegistryClosed) { this.Unwind(t, transaction, Faults.RegistryClosed); yield break; }
            }

            yield return Step.At("TransactionMonitor.cs:76 ThrowIfDisposed after registration");
            if (_mutation != DirectMutation.UnfencedRegistration && this.Disposed)
            {
                this.Unwind(t, transaction, Faults.MonitorDisposed);
                yield break;
            }
            if (!queryOnly) this.Slot[t.Id] = transaction; // :77
            result.Value = transaction;
            isNew.Value = true;
        }

        /// <summary>The catch block of GetTransaction (TransactionMonitor.cs:79-90).</summary>
        private void Unwind(ModelThread t, TxnModel transaction, string fault)
        {
            if (transaction != null)
            {
                this.Registry.Remove(transaction);
                transaction.State = TxnState.Disposed;
            }
            this.Locker.ExitTransaction(t.Id);
            t.Fault = fault;
        }

        /// <summary>
        /// TransactionMonitor.ReleaseTransaction (TransactionMonitor.cs:121-152): dispose, remove,
        /// clear the slot, and release the lease only when this call removed the registration.
        /// </summary>
        public void ReleaseTransaction(ModelThread t, TxnModel transaction)
        {
            if (transaction.State == TxnState.Active) transaction.State = TxnState.Disposed;
            var removed = this.Registry.Remove(transaction);
            if (!transaction.QueryOnly && this.Slot.TryGetValue(transaction.Owner, out var slot) && slot == transaction)
                this.Slot.Remove(transaction.Owner);
            if (removed) this.Locker.ExitTransaction(transaction.Owner);
        }

        /// <summary>TransactionMonitor.Dispose (TransactionMonitor.cs:208-220) with TransactionRegistry.Close (:51-63).</summary>
        public IEnumerable<Step> Dispose(ModelThread t, LifetimeLedger ledger, bool connection)
        {
            if (this.Disposed) yield break; // :210 Interlocked.Exchange
            this.Disposed = true;
            yield return Step.At("TransactionRegistry.cs:53 close the registry and drain every slot");
            this.RegistryClosed = true;
            ledger.FenceAcquired($"gen{this.Generation}", "TransactionRegistry.cs:53");
            if (connection) ledger.FenceAcquired("connection", "TransactionRegistry.cs:53 (Dispose)");
            foreach (var transaction in this.Registry)
            {
                // TransactionService.Dispose (TransactionService.cs:462-495): a foreign thread
                // does not release the owner's thread-affine collection lock.
                transaction.State = TxnState.Disposed;
            }
            this.Registry.Clear();
        }

        public override string ToString() =>
            $"monitor#{this.Generation}({(this.Disposed ? "disposed, " : "")}{(this.RegistryClosed ? "registry closed, " : "")}" +
            $"registered=[{string.Join(", ", this.Registry)}])";
    }
}
