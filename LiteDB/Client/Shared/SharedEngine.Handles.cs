using System;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private SharedHandleLease _handleLease;
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
                var identity = TransactionLease.Handle(new object());
                SharedLocalTransactionOwner.Lease local = null;
                SharedMutexPin pin = null;
                var held = false;
                try
                {
                    local = (_localTransactions ?? _admission?.RegistryKey.HandleAdmissions)?.Enter(identity, SharedHandleTimeout);
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
                    return _handleLease = new SharedHandleLease(this, pin, identity, local, context);
                }
                catch
                {
                    if (pin != null) this.CloseDatabase(pin, hold: held);
                    local?.Dispose();
                    throw;
                }
            });
        }

        private EngineSettings RetainedCoreSettings()
        {
            var settings = _settings.Clone();
            var weak = new WeakReference(this, true);
            settings.AutoRebuildAllowed = () => (weak.Target as SharedEngine)?._readers.OldestVersion().HasValue == false;
            if (_settings.ReadTransform != null)
            {
                var transform = new WeakReference<Func<string, BsonValue, BsonValue>>(_settings.ReadTransform);
                settings.ReadTransform = (collection, value) => transform.TryGetTarget(out var callback)
                    ? callback(collection, value) : throw new ObjectDisposedException(nameof(SharedEngine));
            }
#if NET8_0_OR_GREATER
            if (_readers is SharedMemoryReaders mapped)
            {
                var table = mapped.Table;
                settings.SharedMaintenance = () => table.Maintain(settings);
            }
#endif
            return settings;
        }

        // Holder infrastructure must not keep an abandoned facade (and callback
        // closures capturing its database) alive. Finalization schedules normal
        // cleanup; resurrection tracking keeps the callback available until it drains.
        private Func<bool> WeakPinWaiters()
        {
            var weak = new WeakReference(this, true);
            return () => (weak.Target as SharedEngine)?.HasMutexWaiters() ?? false;
        }

        private Action<SharedMutexPin, bool> WeakPinClose()
        {
            var weak = new WeakReference(this, true);
            return (pin, abandoned) => (weak.Target as SharedEngine)?.ClosePin(pin, abandoned);
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
#if NET8_0_OR_GREATER
            private readonly long _tenure;
#endif
            internal readonly TransactionContext Context;

            internal SharedHandleLease(SharedEngine shared, SharedMutexPin pin, TransactionLease identity,
                SharedLocalTransactionOwner.Lease local, TransactionContext context)
            {
                _shared = shared; _pin = pin; _identity = identity; _local = local; Context = context;
#if NET8_0_OR_GREATER
                _tenure = (shared._readers as SharedMemoryReaders)?.Table.WriterTenure ?? 0;
#endif
            }

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
                    if (_shared._mappedWriterHeld)
                    {
                        _shared.EnterMappedWriter();
#if NET8_0_OR_GREATER
                        if ((_shared._readers as SharedMemoryReaders)?.Table.WriterTenure != _tenure)
                            throw new InvalidOperationException("Stale mapped handle tenure.");
#endif
                    }
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
                if (ReferenceEquals(_shared._handleLease, this)) _shared._handleLease = null;
                // Detach logical identity before the final hold release, while
                // the completing operation still excludes a successor.
                _pin.UnbindHandle(_identity);
                try { _shared.CloseDatabase(_pin, hold: true); }
                finally { Interlocked.Exchange(ref _local, null)?.Dispose(); }
            }
        }
    }
}
