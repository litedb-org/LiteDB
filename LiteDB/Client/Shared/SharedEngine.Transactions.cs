using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Cached entries are inert managed metadata: they own no native admission or files.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> TransactionWriters =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        // A handle's private connection: its storage core is used only by that handle,
        // under native ownership held by the handle's dedicated holder thread.
        private bool _transactionChild;

        /// <summary>
        /// Whether the current thread retains this connection's native ownership outside an
        /// executing call: a legacy transaction or a reader holding the mutex or its pin.
        /// </summary>
        private bool CannotWaitForOwnershipOnCurrentThread() =>
            _owner.IsOwnedByCurrentThread || _pin?.IsHeldByCurrentThread == true;

        /// <summary>
        /// Open the storage of a new transaction handle. A dedicated holder thread acquires
        /// the native writer mutex and opens a fresh core; application threads then run the
        /// handle's operations on that core in turn, independent of which thread they are.
        /// </summary>
        internal TransactionResources OpenTransactionResources()
        {
            // A caller stream can capture the facade; a native holder must not root that
            // graph indefinitely, or perform storage I/O after its external owner is gone.
            if (_settings.Filename == ":memory:" || _settings.Filename == ":temp:" || _settings.DataStream != null ||
                _settings.LogStream != null || _settings.TempStream != null)
                throw new NotSupportedException("Shared transaction handles require filename-backed storage without caller streams.");
            TransactionHolderContext.Validate();
            // An executing handle callback, or an executing call retaining native ownership on
            // this thread, cannot complete while this begin waits behind it.
            TransactionContext.ThrowIfSharedWait(_mutexName);
            if (SharedCallFrames.RetainedByOther(_mutexName, connection: null))
                throw new InvalidOperationException("Cannot open a transaction handle from inside an operation retaining its shared writer ownership.");
            // First-use default collation must observe the caller's culture, while null
            // settings still accept an existing database's persisted collation.
            RuntimeHelpers.RunClassConstructor(typeof(Collation).TypeHandle);
            lock (_useLock)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(SharedEngine));
                if (this.CannotWaitForOwnershipOnCurrentThread())
                    throw new InvalidOperationException("Complete the legacy transaction or close its locking reader before opening a transaction handle.");
            }
            // A second handle of the flow that holds the first may be refused (SharedSelfWaitGrace).
            var selfWait = this.IsSelfWait();
            var gate = TransactionWriters.GetOrAdd(_mutexName, _ => new SemaphoreSlim(1, 1));
            // One SharedWriterTimeout budget covers this local queue and native admission.
            var deadline = SharedWaitDeadline.Start(_settings.SharedWriterTimeout);
            // Pending begins wait on their own thread, never on a holder thread or engine.
            if (!gate.Wait(0)) this.WaitForHandleGate(gate, deadline, selfWait);
            // Database disposal does not wait for a begin queued here: a connection disposed
            // meanwhile must not open storage (recovery, file creation) after Dispose returned.
            lock (_useLock)
                if (_disposed != 0) { gate.Release(); throw new ObjectDisposedException(nameof(SharedEngine)); }
            TransactionHolder holder;
            var policyAnchor = _settings.ReadTransform;
            try
            {
                var settings = _settings.SnapshotForTransactionHolder();
                settings.CoordinationSignals = null;
                settings.SharedFileHandles = null;
                settings.SharedSlowWait = null;
                holder = new TransactionHolder(this.CheckoutTransactionChild(settings, policyAnchor), gate, deadline, this);
            }
            catch { gate.Release(); throw; }
            var resources = holder.Open(policyAnchor);
            _recoveryReport = holder.RecoveryReport ?? _recoveryReport;
            return resources;
        }

        private void WaitForHandleGate(SemaphoreSlim gate, SharedWaitDeadline deadline, bool selfWait)
        {
            var waits = this.Waits;
            var wait = waits.Begin();
            var acquired = false;
            var refused = true;
            try
            {
                while (!(acquired = gate.Wait((selfWait ? deadline.Within(_settings.SharedSelfWaitGrace) : deadline).RemainingMilliseconds)) &&
                    selfWait && !deadline.Expired)
                    selfWait = this.StillSelfWaiting();
                refused = false;
            }
            finally { waits.End(wait, timedOut: !acquired && !refused); }
            if (!acquired) throw waits.TimeoutError(deadline.Timeout);
        }
    }
}
