using System;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private long _handleTimeoutTicks = TimeSpan.TicksPerMinute;

        // Safe observations only: never read a dirty header to bootstrap admission.
        private TimeSpan SharedHandleTimeout => TimeSpan.FromTicks(Interlocked.Read(ref _handleTimeoutTicks));

        internal SharedHandleLease BeginHandle()
        {
            using var budget = new TransactionWaitBudget(SharedHandleTimeout);
            return this.Call(() =>
            {
                this.ThrowIfForeignLocalTransaction(writing: true);
                if (_transactionRunning && ReferenceEquals(_transactionThread, Thread.CurrentThread))
                    throw LiteException.SameThreadTransaction();
                this.ThrowIfCallerRetainsOwnership();
                var identity = TransactionLease.Handle(this);
                SharedLocalTransactionOwner.Lease local = null;
                SharedMutexPin pin = null;
                var held = false;
                try
                {
                    local = _localTransactions?.Enter(identity, SharedHandleTimeout);
                    // Reuse only an idle tenure. A live legacy reader/transaction is
                    // never converted into a handle's independent transaction.
                    var retained = _pin;
                    pin = retained?.IsIdle == true && this.TryEnterPin(retained) ? retained : this.StartPin();
                    var timeout = _engine.HandleTimeout;
                    Interlocked.Exchange(ref _handleTimeoutTicks, timeout.Ticks);
                    TransactionWaitBudget.Clamp(timeout);
                    var context = _engine.BeginHandleTransaction();
                    pin.BindHandle(identity);
                    pin.ToHold();
                    held = true;
                    return new SharedHandleLease(this, pin, identity, local, context);
                }
                catch
                {
                    if (pin != null) this.CloseDatabase(pin, hold: held);
                    local?.Dispose();
                    throw;
                }
            });
        }

        // One logical lease over the existing facade core/participant. The OS
        // mutex stays on its holder; creator-thread death cannot abandon this lease.
        internal sealed class SharedHandleLease : IDisposable
        {
            private readonly SharedEngine _shared;
            private readonly SharedMutexPin _pin;
            private readonly TransactionLease _identity;
            private SharedLocalTransactionOwner.Lease _local;
            private IDisposable _frame;
            private int _depth;
            private bool _released;
            internal readonly TransactionContext Context;

            internal SharedHandleLease(SharedEngine shared, SharedMutexPin pin, TransactionLease identity,
                SharedLocalTransactionOwner.Lease local, TransactionContext context)
            { _shared = shared; _pin = pin; _identity = identity; _local = local; Context = context; }

            internal void Enter()
            {
                _shared.ThrowIfTeardownReentry();
                _depth = _shared.AdmittedDepth();
                if (!_pin.TryEnter(_identity)) throw new InvalidOperationException("The Shared handle tenure ended.");
                try
                {
                    _shared._admission?.EnsureValid();
                    lock (_shared._useLock)
                    {
                        _shared.AdmitLocked();
                        if (!ReferenceEquals(_shared._pin, _pin) || !ReferenceEquals(_shared._engine, Context.Engine))
                            throw new InvalidOperationException("Stale Shared handle generation.");
                    }
                    if (_shared._mappedWriterHeld) _shared.EnterMappedWriter();
                    _frame = _shared.OwnershipFrame(_shared.CallRetains);
                }
                catch { _shared.EndAdmissions(_depth); _pin.Exit(false); throw; }
            }

            internal void Exit()
            {
                try { _frame?.Dispose(); _frame = null; }
                finally
                {
                    _shared.EndAdmissions(_depth);
                    _shared.CloseDatabase(_pin);
                }
            }

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                // Detach logical identity before the final hold release, while
                // the completing operation still excludes a successor.
                _pin.UnbindHandle(_identity);
                try { _shared.CloseDatabase(_pin, hold: true); }
                finally { Interlocked.Exchange(ref _local, null)?.Dispose(); }
            }
        }
    }
}
