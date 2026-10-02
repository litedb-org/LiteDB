using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Direct
{
    /// <summary>
    /// The Direct engine's maintenance paths: <c>LiteEngine.Dispose</c>/<c>Close</c>
    /// (LiteEngine.cs:211-238, 322-331), <c>Rebuild</c> (Rebuild.cs:19-71) and the fatal stop
    /// (<c>EngineState.Stop</c>, EngineState.cs:404-430, with <c>Close(ex)</c>, LiteEngine.cs:261-288).
    /// </summary>
    internal sealed class DirectLifecycle
    {
        private readonly DirectEngineModel _engine;

        public DirectLifecycle(DirectEngineModel engine) => _engine = engine;

        private LifetimeLedger Ledger => _engine.Ledger;

        public IEnumerable<Step> Run(ModelThread t, OpKind kind)
        {
            switch (kind)
            {
                case OpKind.Close: return this.Dispose(t);
                case OpKind.Rebuild: return this.Rebuild(t);
                case OpKind.Fatal: return this.Fatal(t);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private IEnumerable<Step> Dispose(ModelThread t)
        {
            var op = this.Ledger.Begin(t, OpKind.Close, "LiteEngine.Dispose");
            foreach (var step in this.Close(t, user: true)) yield return step;
            this.Ledger.End(op, OpOutcome.Completed, null, "LiteEngine.cs:330 Dispose returns");
        }

        /// <summary>LiteEngine.Close(checkpoint: true) (LiteEngine.cs:211-238).</summary>
        private IEnumerable<Step> Close(ModelThread t, bool user)
        {
            yield return Step.At("LiteEngine.cs:213 Close: if (_state.Disposed) return");
            var state = _engine.State;
            if (state.Disposed)
            {
                // The caller's Dispose returns: from its point of view the connection is closed now.
                if (user) this.Ledger.FenceAcquired("connection", "LiteEngine.cs:213 Dispose returned early (already disposed)");
                yield break;
            }
            yield return Step.At("LiteEngine.cs:215 _state.Disposed = true");
            state.Disposed = true;
            this.Ledger.DoomTransactions("LiteEngine.cs:215");

            yield return Step.At("LiteEngine.cs:220 TransactionMonitor.Dispose");
            var monitor = _engine.Monitor;
            foreach (var step in this.DisposeMonitor(t, monitor, user)) yield return step;

            if (_engine.LogHasContent)
            {
                // WalIndexService.TryCloseCheckpoint → LockService.TryEnterExclusive(waitForReaders, 10 ms).
                yield return Step.At("LiteEngine.cs:225 TryCloseCheckpoint: LockService.TryEnterExclusive (LockService.cs:125)");
                var locker = monitor.Locker;
                if (locker.Writer != t.Id && !locker.Readers.ContainsKey(t.Id))
                {
                    foreach (var step in locker.EnterWriteLock(t, DirectEngineModel.ReaderWait, "checkpoint skipped")) yield return step;
                    if (t.Fault == null)
                    {
                        yield return Step.At("WalIndexService.Checkpoint.cs checkpoint under exclusive, then ExitExclusive");
                        locker.ExitWriteLock(t);
                    }
                    t.Fault = null; // TryCatch collects close errors (LiteEngine.cs:217).
                }
            }

            yield return Step.At("LiteEngine.cs:235 LockService.Dispose");
            _engine.Locker.Dispose();
        }

        /// <summary>
        /// TransactionMonitor.Dispose with the closing thread's own transaction releasing its
        /// collection lock (TransactionPageCleanup releases locks only on the owner thread).
        /// </summary>
        private IEnumerable<Step> DisposeMonitor(ModelThread t, TransactionMonitorModel monitor, bool user)
        {
            var owned = new List<TxnModel>();
            foreach (var transaction in monitor.Registry)
            {
                if (transaction.Owner == t.Id && transaction.LockedCollection != null) owned.Add(transaction);
            }
            foreach (var step in monitor.Dispose(t, this.Ledger, user)) yield return step;
            foreach (var transaction in owned) monitor.Locker.ExitLock(t, transaction.LockedCollection);
        }

        /// <summary>LiteEngine.Rebuild (Rebuild.cs:19-71).</summary>
        private IEnumerable<Step> Rebuild(ModelThread t)
        {
            var op = this.Ledger.Begin(t, OpKind.Rebuild, "LiteEngine.Rebuild");
            yield return Step.At("Rebuild.cs:35 if (_locker.IsInTransaction) throw");
            var locker = _engine.Locker;
            if (locker.IsInTransaction(t)) { this.Ledger.End(op, OpOutcome.Rejected, Faults.AlreadyInTransaction, "Rebuild.cs:35"); yield break; }

            yield return Step.At("Rebuild.cs:36 LockService.EnterExclusive (LockService.cs:104)");
            if (locker.Writer != t.Id)
            {
                foreach (var step in locker.EnterWriteLock(t, DirectEngineModel.PragmaTimeout, Faults.ExclusiveTimeout)) yield return step;
                if (t.Fault != null) { this.Ledger.End(op, OpOutcome.Rejected, t.Fault, "LockService.cs:113"); t.Fault = null; yield break; }
            }
            this.Ledger.Admitted(op, "Rebuild.cs:36 exclusive admission", $"gen{locker.Generation}");

            yield return Step.At("Rebuild.cs:38 this.Close()");
            foreach (var step in this.Close(t, user: false)) yield return step;
            yield return Step.At("Rebuild.cs:47 RebuildService.Rebuild replaces the files");

            // LiteEngine.Open (LiteEngine.cs:84-194), in the order it publishes the new services.
            yield return Step.At("Rebuild.cs:66 Open: LiteEngine.cs:94 _state = new EngineState");
            _engine.State = new EngineStateModel();
            yield return Step.At("LiteEngine.cs:157 _locker = new LockService");
            _engine.OpenLocker();
            yield return Step.At("LiteEngine.cs:175 _monitor = new TransactionMonitor");
            _engine.OpenMonitor();
            yield return Step.At("Rebuild.cs:68 _state.Disposed = false");
            _engine.State.Disposed = false;
            this.Ledger.End(op, OpOutcome.Completed, null, "Rebuild.cs:70 returns");
        }

        /// <summary>An auto-transaction write whose commit fails with an I/O error: the engine stops.</summary>
        private IEnumerable<Step> Fatal(ModelThread t)
        {
            var op = this.Ledger.Begin(t, OpKind.Fatal, "Insert whose commit fails with an I/O error");
            foreach (var step in _engine.AutoWrite(t, op, "c1", null, injectFailure: true)) yield return step;
            if (!op.Ended) this.Ledger.End(op, OpOutcome.Completed, null, "Transaction.cs:329 returns");
        }

        /// <summary>EngineState.Stop (EngineState.cs:404-430) and LiteEngine.Close(ex, origin) (LiteEngine.cs:261-288).</summary>
        public IEnumerable<Step> Stop(ModelThread t, string fault)
        {
            yield return Step.At("EngineState.cs:413 BeginStop");
            var state = _engine.State;
            if (state.Disposed || state.Failure != null) yield break; // :416, :419
            state.Failure = fault;
            this.Ledger.DoomTransactions("EngineState.cs:419 failure published");

            yield return Step.At("EngineState.cs:428 CompleteStop: LiteEngine.Close(ex, origin)");
            if (state == _engine.State && !state.Disposed) // LiteEngine.cs:263-264
            {
                state.Disposed = true;
                yield return Step.At("LiteEngine.cs:270 TransactionMonitor.Dispose");
                foreach (var step in this.DisposeMonitor(t, _engine.Monitor, user: false)) yield return step;
                yield return Step.At("LiteEngine.cs:285 LockService.Dispose");
                _engine.Locker.Dispose();
            }
            state.Disposed = true; // EngineState.cs:429
        }
    }
}
