using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    public enum SnapshotMode : byte
    {
        /// <summary>The transaction has not touched the collection yet.</summary>
        None = 0,
        /// <summary>Read snapshot pinned at the committed state of the first read.</summary>
        Read = 1,
        /// <summary>Write snapshot: holds the collection lock; view starts from the committed state at acquisition.</summary>
        Write = 2
    }

    /// <summary>
    /// An active transaction of the model. <see cref="OwnerKey"/> identifies who completes it:
    /// a thread index for per-thread (legacy) transactions, any other key for access kinds
    /// whose transactions are not thread-affine. Collection locks are held by owner keys.
    /// </summary>
    public sealed class ModelTransaction
    {
        internal ModelTransaction(int ownerKey, int ownerThread, bool isExplicit, int collections, int keys)
        {
            this.OwnerKey = ownerKey;
            this.OwnerThread = ownerThread;
            this.Explicit = isExplicit;
            this.Modes = new SnapshotMode[collections];
            this.View = new int[collections * keys];
        }

        private ModelTransaction(ModelTransaction source)
        {
            this.OwnerKey = source.OwnerKey;
            this.OwnerThread = source.OwnerThread;
            this.Explicit = source.Explicit;
            this.Modes = (SnapshotMode[])source.Modes.Clone();
            this.View = (int[])source.View.Clone();
        }

        public int OwnerKey { get; }
        /// <summary>Thread that began the transaction (the thread an engine records as owner).</summary>
        public int OwnerThread { get; }
        /// <summary>Opened by an explicit begin call (as opposed to an automatic per-operation transaction).</summary>
        public bool Explicit { get; }
        internal SnapshotMode[] Modes { get; }
        internal int[] View { get; }

        public SnapshotMode ModeOf(int collection) => this.Modes[collection];

        internal ModelTransaction Clone() => new ModelTransaction(this);

        internal void AppendKey(StringBuilder sb)
        {
            sb.Append('{').Append(this.OwnerKey).Append(',').Append(this.OwnerThread).Append(this.Explicit ? 'E' : 'A');
            for (var c = 0; c < this.Modes.Length; c++) sb.Append((int)this.Modes[c]);
            sb.Append(':');
            foreach (var value in this.View) sb.Append(value).Append(',');
            sb.Append('}');
        }
    }

    /// <summary>
    /// The specification state: committed collection contents (integer payload per key, 0 = absent),
    /// active transactions with their snapshots, collection lock holders, per-thread
    /// "explicit transaction was aborted by a failed operation" flags, the thread holding a Shared
    /// connection's mutex across calls, and integer registers for access-kind extensions.
    /// Instances are mutated only by the transition that owns them; transitions clone first.
    /// </summary>
    public sealed class ModelState
    {
        public const int NoOwner = -1;

        private readonly int[] _committed;
        private readonly int[] _lockHolders;
        private readonly List<ModelTransaction> _transactions;
        private readonly SortedDictionary<int, int> _registers;
        private readonly int[] _pending;
        private int _abortFlags;

        public ModelState(ConnectionType mode, int collections, int keys, int threads)
        {
            if (threads < 1 || threads > 30) throw new ArgumentOutOfRangeException(nameof(threads));
            this.Mode = mode;
            this.Collections = collections;
            this.Keys = keys;
            this.Threads = threads;
            _committed = new int[collections * keys];
            _lockHolders = Enumerable.Repeat(NoOwner, collections).ToArray();
            _transactions = new List<ModelTransaction>();
            _registers = new SortedDictionary<int, int>();
            _pending = new int[threads];
            this.SharedHolder = NoOwner;
        }

        private ModelState(ModelState source)
        {
            this.Mode = source.Mode;
            this.Collections = source.Collections;
            this.Keys = source.Keys;
            this.Threads = source.Threads;
            _committed = (int[])source._committed.Clone();
            _lockHolders = (int[])source._lockHolders.Clone();
            _transactions = source._transactions.Select(t => t.Clone()).ToList();
            _registers = new SortedDictionary<int, int>(source._registers);
            _pending = (int[])source._pending.Clone();
            _abortFlags = source._abortFlags;
            this.SharedHolder = source.SharedHolder;
        }

        public ConnectionType Mode { get; }
        public int Collections { get; }
        /// <summary>Key space size: keys are 1..Keys.</summary>
        public int Keys { get; }
        public int Threads { get; }

        /// <summary>Shared mode: thread whose explicit transaction keeps the connection's mutex across calls.</summary>
        public int SharedHolder { get; set; }

        public IReadOnlyList<ModelTransaction> Transactions => _transactions;

        public ModelState Clone() => new ModelState(this);

        private int Index(int collection, int key) => collection * this.Keys + (key - 1);

        public int Committed(int collection, int key) => _committed[this.Index(collection, key)];

        public void SetCommitted(int collection, int key, int payload) => _committed[this.Index(collection, key)] = payload;

        /// <summary>Canonical committed contents of a collection, comparable with <see cref="DataOperations.Canonical"/>.</summary>
        public string CommittedContents(int collection) => this.Contents(_committed, collection);

        internal string Contents(int[] values, int collection)
        {
            var sb = new StringBuilder("[");
            for (var key = 1; key <= this.Keys; key++)
            {
                var payload = values[this.Index(collection, key)];
                if (payload == 0) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append(key).Append(':').Append(payload);
            }
            return sb.Append(']').ToString();
        }

        public ModelTransaction Transaction(int ownerKey) => _transactions.FirstOrDefault(t => t.OwnerKey == ownerKey);

        public ModelTransaction Begin(int ownerKey, int ownerThread, bool isExplicit)
        {
            if (this.Transaction(ownerKey) != null) throw new InvalidOperationException("Owner already has a transaction: " + ownerKey);
            var transaction = new ModelTransaction(ownerKey, ownerThread, isExplicit, this.Collections, this.Keys);
            _transactions.Add(transaction);
            _transactions.Sort((a, b) => a.OwnerKey.CompareTo(b.OwnerKey));
            return transaction;
        }

        public int LockHolder(int collection) => _lockHolders[collection];

        /// <summary>Whether <paramref name="transaction"/> (null: an automatic one) can write the collection now.</summary>
        public bool CanWrite(ModelTransaction transaction, int collection)
        {
            var holder = _lockHolders[collection];
            return holder == NoOwner || (transaction != null && holder == transaction.OwnerKey);
        }

        /// <summary>
        /// Take the collection lock and a write snapshot. Like the engine, upgrading from a read
        /// snapshot replaces it with a fresh view of the latest committed state.
        /// </summary>
        public void AcquireWrite(ModelTransaction transaction, int collection)
        {
            if (!this.CanWrite(transaction, collection)) throw new InvalidOperationException("Lock is held by another owner.");
            _lockHolders[collection] = transaction.OwnerKey;
            if (transaction.Modes[collection] == SnapshotMode.Write) return;
            this.CopyCommitted(transaction, collection);
            transaction.Modes[collection] = SnapshotMode.Write;
        }

        /// <summary>Pin a read snapshot on first access; later reads keep seeing it.</summary>
        public void EnsureSnapshot(ModelTransaction transaction, int collection)
        {
            if (transaction.Modes[collection] != SnapshotMode.None) return;
            this.CopyCommitted(transaction, collection);
            transaction.Modes[collection] = SnapshotMode.Read;
        }

        private void CopyCommitted(ModelTransaction transaction, int collection)
        {
            Array.Copy(_committed, this.Index(collection, 1), transaction.View, this.Index(collection, 1), this.Keys);
        }

        /// <summary>Value visible to <paramref name="transaction"/> (pins a snapshot) or, when null, the committed value.</summary>
        public int Read(ModelTransaction transaction, int collection, int key)
        {
            if (transaction == null) return this.Committed(collection, key);
            this.EnsureSnapshot(transaction, collection);
            return transaction.View[this.Index(collection, key)];
        }

        /// <summary>Write through a transaction's write snapshot or, when null, directly to the committed state.</summary>
        public void Write(ModelTransaction transaction, int collection, int key, int payload)
        {
            if (transaction == null)
            {
                _committed[this.Index(collection, key)] = payload;
                return;
            }
            if (transaction.Modes[collection] != SnapshotMode.Write) throw new InvalidOperationException("Write without write snapshot.");
            transaction.View[this.Index(collection, key)] = payload;
        }

        /// <summary>Publish every write snapshot atomically, release the locks and end the transaction.</summary>
        public void Commit(ModelTransaction transaction)
        {
            for (var c = 0; c < this.Collections; c++)
            {
                if (transaction.Modes[c] == SnapshotMode.Write)
                    Array.Copy(transaction.View, this.Index(c, 1), _committed, this.Index(c, 1), this.Keys);
            }
            this.End(transaction);
        }

        /// <summary>Discard the transaction's writes, release its locks and end it.</summary>
        public void Rollback(ModelTransaction transaction) => this.End(transaction);

        private void End(ModelTransaction transaction)
        {
            for (var c = 0; c < this.Collections; c++)
            {
                if (_lockHolders[c] == transaction.OwnerKey) _lockHolders[c] = NoOwner;
            }
            if (!_transactions.Remove(transaction)) throw new InvalidOperationException("Transaction is not active.");
        }

        public bool AbortFlag(int thread) => (_abortFlags & (1 << thread)) != 0;

        public void SetAbortFlag(int thread, bool value)
        {
            if (value) _abortFlags |= 1 << thread;
            else _abortFlags &= ~(1 << thread);
        }

        /// <summary>Read and clear the abort flag, like the engine's ConsumeExplicitAbort.</summary>
        public bool ConsumeAbortFlag(int thread)
        {
            var value = this.AbortFlag(thread);
            this.SetAbortFlag(thread, false);
            return value;
        }

        /// <summary>Phase of a thread's operation that took effect only partly so far (0 = none); see <see cref="ModelOutcome.Completes"/>.</summary>
        public int Pending(int thread) => _pending[thread];

        public void SetPending(int thread, int phase) => _pending[thread] = phase;

        /// <summary>Extension registers for access kinds (default 0); part of the state's identity.</summary>
        public int Register(int key) => _registers.TryGetValue(key, out var value) ? value : 0;

        public void SetRegister(int key, int value)
        {
            if (value == 0) _registers.Remove(key);
            else _registers[key] = value;
        }

        /// <summary>A string that identifies the state, used to memoize the interleaving search.</summary>
        public string Key()
        {
            var sb = new StringBuilder();
            foreach (var value in _committed) sb.Append(value).Append(',');
            sb.Append('|');
            foreach (var holder in _lockHolders) sb.Append(holder).Append(',');
            sb.Append('|').Append(_abortFlags).Append('|').Append(this.SharedHolder).Append('|');
            foreach (var phase in _pending) sb.Append(phase).Append(',');
            foreach (var transaction in _transactions) transaction.AppendKey(sb);
            foreach (var register in _registers) sb.Append(register.Key).Append('=').Append(register.Value).Append(';');
            return sb.ToString();
        }

        public override string ToString()
        {
            var parts = Enumerable.Range(0, this.Collections).Select(c => "c" + c + "=" + this.CommittedContents(c)).ToList();
            foreach (var transaction in _transactions)
                parts.Add($"tx(owner={transaction.OwnerKey}, modes={string.Join("", transaction.Modes.Select(m => m.ToString()[0]))})");
            for (var c = 0; c < this.Collections; c++)
                if (_lockHolders[c] != NoOwner) parts.Add($"lock(c{c})={_lockHolders[c]}");
            if (this.SharedHolder != NoOwner) parts.Add("sharedHolder=T" + this.SharedHolder);
            if (_abortFlags != 0) parts.Add("abortFlags=" + _abortFlags);
            for (var t = 0; t < _pending.Length; t++)
                if (_pending[t] != 0) parts.Add($"pending(T{t})={_pending[t]}");
            return string.Join(" ", parts);
        }
    }
}
