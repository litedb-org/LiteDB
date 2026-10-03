using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Direct
{
    /// <summary>Exceptions the Direct paths can raise, by the type and error the real code throws.</summary>
    internal static class Faults
    {
        public const string EngineDisposed = "LiteException(ENGINE_DISPOSED)";
        public const string MonitorDisposed = "ObjectDisposedException(TransactionMonitor)";
        public const string RegistryClosed = "ObjectDisposedException(TransactionRegistry)";
        public const string GateDisposed = "ObjectDisposedException(TransactionGate)";
        public const string TransactionTimeout = "LiteException(LOCK_TIMEOUT transaction)";
        public const string ExclusiveTimeout = "LiteException(LOCK_TIMEOUT exclusive)";
        public const string WriteTimeout = "LiteException(LOCK_TIMEOUT write)";
        public const string AlreadyInTransaction = "LiteException(ALREADY_EXISTS_TRANSACTION)";
        public const string TransactionDisposed = "ObjectDisposedException(TransactionService)";
        public const string InjectedIo = "IOException(injected)";
    }

    /// <summary>
    /// One <c>LockService</c> instance (LiteDB/Engine/Services/LockService.cs) with its
    /// <c>TransactionGate</c> (TransactionGate.cs) and per-collection <c>CollectionLock</c>s
    /// (CollectionLock.cs). Rebuild replaces it with a new instance (LiteEngine.Open).
    /// Thread identity is the model thread id, as the real gate keys leases by <c>Thread</c>.
    /// </summary>
    internal sealed class LockServiceModel
    {
        private readonly DirectMutation _mutation;
        private readonly Dictionary<string, int> _collectionOwner = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _collectionDepth = new Dictionary<string, int>();

        public LockServiceModel(int generation, DirectMutation mutation)
        {
            this.Generation = generation;
            _mutation = mutation;
        }

        public int Generation { get; }

        // TransactionGate fields (TransactionGate.cs:192-196).
        public Dictionary<int, int> Readers { get; } = new Dictionary<int, int>();

        public int ReaderCount { get; private set; }

        public int? Writer { get; private set; }

        public int WaitingWriters { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>LockService.IsInTransaction (LockService.cs:37).</summary>
        public bool IsInTransaction(ModelThread t) => this.Readers.ContainsKey(t.Id) || this.Writer == t.Id;

        /// <summary>TransactionGate.cs:228: a writer holds or waits, and this thread holds no lease yet.</summary>
        private bool ReadBlocked(int thread) =>
            this.Writer != null || (this.WaitingWriters != 0 &&
                (_mutation == DirectMutation.NestedLeaseNotExempt || !this.Readers.ContainsKey(thread)));

        /// <summary>LockService.EnterTransaction (LockService.cs:47-67) and TransactionGate.TryEnterReadLock (:221-237).</summary>
        public IEnumerable<Step> EnterTransaction(ModelThread t, int timeout)
        {
            // LockService.cs:53 a thread in exclusive mode takes no lease.
            if (this.Writer == t.Id) yield break;
            // TransactionGate.cs:227 ThrowIfDisposed, mapped to EngineDisposed by LockService.cs:60-65.
            if (this.Disposed)
            {
                t.Fault = Faults.EngineDisposed;
                yield break;
            }
            const string site = "TransactionGate.cs:228 TryEnterReadLock waits for writers";
            bool Ready() => this.Disposed || !this.ReadBlocked(t.Id);
            yield return _mutation == DirectMutation.UnboundedGateWaits ? Step.Wait(site, Ready) : Step.TimedWait(site, timeout, Ready);
            if (t.TimedOut)
            {
                t.Fault = Faults.TransactionTimeout; // LockService.cs:57-58
                yield break;
            }
            if (this.Disposed)
            {
                t.Fault = Faults.EngineDisposed; // TransactionGate.cs:296 after the wait, LockService.cs:65
                yield break;
            }
            this.Readers[t.Id] = this.Readers.TryGetValue(t.Id, out var count) ? count + 1 : 1; // :232-234
            this.ReaderCount++;
        }

        /// <summary>TransactionGate.ExitReadLock (TransactionGate.cs:239-251); any thread may release the owner's lease.</summary>
        public void ExitTransaction(int owner)
        {
            if (this.Writer == owner || !this.Readers.TryGetValue(owner, out var count)) return;
            if (count == 1) this.Readers.Remove(owner);
            else this.Readers[owner] = count - 1;
            this.ReaderCount--;
        }

        /// <summary>
        /// TransactionGate.TryEnterWriteLock (TransactionGate.cs:253-277), called by
        /// LockService.EnterExclusive (LockService.cs:104-119) and TryEnterExclusive (:125-153).
        /// </summary>
        public IEnumerable<Step> EnterWriteLock(ModelThread t, int timeout, string timeoutFault)
        {
            // Only the pragma-bounded exclusive wait (Rebuild) loses its bound in the mutation.
            var bounded = _mutation != DirectMutation.UnboundedGateWaits || timeout != DirectEngineModel.PragmaTimeout;
            if (this.Disposed)
            {
                t.Fault = Faults.GateDisposed; // :259
                yield break;
            }
            this.WaitingWriters++; // :261
            const string site = "TransactionGate.cs:264 TryEnterWriteLock waits for leases to drain";
            bool Ready() => this.Disposed || (this.Writer == null && this.ReaderCount == 0);
            yield return bounded ? Step.TimedWait(site, timeout, Ready) : Step.Wait(site, Ready);
            this.WaitingWriters--; // finally :273
            if (t.TimedOut)
            {
                t.Fault = timeoutFault;
                yield break;
            }
            if (this.Disposed)
            {
                t.Fault = Faults.GateDisposed; // :296
                yield break;
            }
            this.Writer = t.Id; // :268
        }

        /// <summary>TransactionGate.ExitWriteLock (TransactionGate.cs:281-289).</summary>
        public void ExitWriteLock(ModelThread t)
        {
            if (this.Writer == t.Id) this.Writer = null;
        }

        /// <summary>LockService.EnterLock (LockService.cs:80-88): a reentrant Monitor with the pragma timeout.</summary>
        public IEnumerable<Step> EnterLock(ModelThread t, string collection, int timeout)
        {
            yield return Step.TimedWait($"CollectionLock.cs:338 Monitor.TryEnter({collection})", timeout,
                () => !_collectionOwner.TryGetValue(collection, out var owner) || owner == t.Id);
            if (t.TimedOut)
            {
                t.Fault = Faults.WriteTimeout; // LockService.cs:87
                yield break;
            }
            _collectionOwner[collection] = t.Id;
            _collectionDepth[collection] = _collectionDepth.TryGetValue(collection, out var depth) ? depth + 1 : 1;
        }

        /// <summary>LockService.ExitLock (LockService.cs:93-98), run when the owner's snapshot is disposed.</summary>
        public void ExitLock(ModelThread t, string collection)
        {
            if (!_collectionOwner.TryGetValue(collection, out var owner) || owner != t.Id) return;
            if (--_collectionDepth[collection] > 0) return;
            _collectionOwner.Remove(collection);
            _collectionDepth.Remove(collection);
        }

        /// <summary>LockService.Dispose → TransactionGate.Dispose (TransactionGate.cs:305-314).</summary>
        public void Dispose()
        {
            this.Disposed = true;
            this.Readers.Clear();
            this.ReaderCount = 0;
        }

        public override string ToString() =>
            $"gate#{this.Generation}(readers={this.ReaderCount}, writer={this.Writer?.ToString() ?? "-"}, waitingWriters={this.WaitingWriters}" +
            $"{(this.Disposed ? ", disposed" : "")}, collectionLocks={string.Join("/", _collectionOwner)})";
    }
}
