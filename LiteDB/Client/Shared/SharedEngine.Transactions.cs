using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
            var gate = TransactionWriters.GetOrAdd(_mutexName, _ => new SemaphoreSlim(1, 1));
            // Pending begins wait on their own thread, never on a holder thread or engine.
            gate.Wait();
            TransactionHolder holder;
            var policyAnchor = _settings.ReadTransform;
            try
            {
                var settings = _settings.SnapshotForTransactionHolder();
                // The holder thread must not root application callbacks that can capture the
                // facade/handle. The external resource owner retains the delegate while live.
                if (policyAnchor != null)
                {
                    var callback = new WeakReference<Func<string, BsonValue, BsonValue>>(policyAnchor);
                    settings.ReadTransform = (collection, value) => callback.TryGetTarget(out var transform)
                        ? transform(collection, value) : throw new ObjectDisposedException("Transaction read policy");
                }
                settings.CoordinationSignals = null;
                settings.SharedFileHandles = null;
                var child = new SharedEngine(settings) { _transactionChild = true };
                child._settings.SharedDurability = _settings.SharedDurability;
                child._settings.CheckpointBackoff = _settings.CheckpointBackoff;
                holder = new TransactionHolder(child, gate);
            }
            catch { gate.Release(); throw; }
            return holder.Open(policyAnchor);
        }

        /// <summary>One internal native owner per handle, independent of application threads.</summary>
        private sealed class TransactionHolder
        {
            private readonly SharedEngine _child;
            private readonly SemaphoreSlim _gate;
            private readonly ManualResetEventSlim _opened = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _close = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim();
            private Exception _error;
            private LiteEngine _engine;

            internal TransactionHolder(SharedEngine child, SemaphoreSlim gate)
            {
                _child = child;
                _gate = gate;
            }

            internal TransactionResources Open(object policyAnchor)
            {
                try
                {
                    var thread = new Thread(this.Run) { IsBackground = true, Name = "LiteDB shared transaction holder" };
                    // The holder must not retain application AsyncLocals that could keep an
                    // abandoned facade, and so this holder's writer ownership, alive.
                    if (ExecutionContext.IsFlowSuppressed()) thread.Start();
                    else using (ExecutionContext.SuppressFlow()) thread.Start();
                }
                catch (Exception error)
                {
                    try { _child.Dispose(); }
                    catch (Exception cleanup) { error.Data["LiteDB.TransactionOpenCleanup"] = cleanup; }
                    finally { _gate.Release(); }
                    throw;
                }
                // An interrupted begin must still let the holder release what it acquires:
                // no resources exist yet whose disposal or finalizer could do it later.
                try { _opened.Wait(); }
                catch { _close.Set(); throw; }
                if (_error != null) this.Release();
                return new TransactionResources(_engine, this.Release, () => _close.Set(), policyAnchor, _child._mutexName);
            }

            private void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error)
                {
                    if (_error == null) _error = error;
                    else _error.Data["LiteDB.SharedCleanup." + _error.Data.Count] = error;
                }
            }

            private void Run()
            {
                var acquired = false;
                try
                {
                    _child.OpenDatabase(scoped: true, writing: !_child._settings.ReadOnly);
                    acquired = true;
                    _engine = _child._engine;
                    _opened.Set();
                    _close.Wait();
                }
                catch (Exception error) { _error = error; }
                finally
                {
                    // Closing the core without commit discards uncommitted work, then
                    // releases native writer ownership on the thread that owns it.
                    if (acquired) this.Cleanup(() => _child.CloseDatabase());
                    this.Cleanup(() => _child.EndAdmissions(0));
                    this.Cleanup(_child.Dispose);
                    _gate.Release();
                    // Failed-open publication follows all cleanup and preserves its original error.
                    _opened.Set();
                    _done.Set();
                }
            }

            private void Release()
            {
                _close.Set();
                _done.Wait();
                if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
            }
        }
    }
}
