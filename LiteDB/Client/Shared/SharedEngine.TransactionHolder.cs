using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        /// <summary>
        /// One internal native owner per handle, independent of application threads. It runs as a job
        /// of <see cref="SharedHolderScheduler"/> and takes and releases the native mutex within that
        /// job. Its events are per handle, so a stale signal never reaches a later handle.
        /// </summary>
        private sealed class TransactionHolder
        {
            private readonly SharedEngine _child;
            // An abandoned handle must let its connection be collected while this job still waits
            // for the handle's finalizer: the cache owner is reached only weakly.
            private readonly WeakReference<SharedEngine> _cacheOwner;
            private readonly SemaphoreSlim _gate;
            private readonly SharedWaitDeadline _deadline;
            // Created once ownership is acquired, so held/idle durations exclude the admission wait.
            private SharedHandleActivity _activity;
            private readonly ManualResetEventSlim _opened = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _close = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim();
            private Exception _error;
            private LiteEngine _engine;
            private bool _coreFailed;

            internal TransactionHolder(SharedEngine child, SemaphoreSlim gate, SharedWaitDeadline deadline, SharedEngine cacheOwner)
            {
                _child = child;
                _gate = gate;
                _deadline = deadline;
                _cacheOwner = new WeakReference<SharedEngine>(cacheOwner);
            }

            /// <summary>The recovery report of the handle's core, once it opened.</summary>
            internal WalRecoveryReport RecoveryReport => _child._recoveryReport;

            /// <summary>When the holder's native wait ended, or zero if it never ran.</summary>
            internal long AdmittedAt => Volatile.Read(ref _child._admittedAt);

            internal TransactionResources Open(object policyAnchor)
            {
                // The holder must not retain application AsyncLocals that could keep an abandoned
                // facade, and so this holder's writer ownership, alive: jobs run in a clean context.
                try { SharedHolderScheduler.Queue(this.Run); }
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
                return new TransactionResources(_engine, this.Release, () => _close.Set(), policyAnchor, _child._mutexName, _activity);
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
#if DEBUG || TESTING
                _child.HolderThread = Thread.CurrentThread;
#endif
                try
                {
                    using (_deadline.Inherit()) _child.OpenDatabase(scoped: true, writing: !_child._settings.ReadOnly);
                    acquired = true;
                    _engine = _child._engine;
                    SharedHandleRegistry.Register(_child._mutexName, _activity = new SharedHandleActivity());
                    _opened.Set();
                    _close.Wait();
                }
                catch (Exception error) { _error = error; }
                finally
                {
                    // Closing the core without commit discards uncommitted work, then
                    // releases native writer ownership on the thread that owns it.
                    // A core that stopped on a failure (a failed WAL write) discards its wrapper too.
                    _coreFailed = _engine?.HasFailed == true;
                    if (acquired) this.Cleanup(() => _child.CloseDatabase());
                    if (_activity != null) SharedHandleRegistry.Unregister(_child._mutexName, _activity);
                    this.Cleanup(() => _child.EndAdmissions(0));
                    // Before the gate opens, so the next begin of this connection finds the wrapper.
                    this.ReturnOrDispose();
                    _gate.Release();
                    // Failed-open publication follows all cleanup and preserves its original error.
                    _opened.Set();
                    _done.Set();
                }
            }

            /// <summary>
            /// Cache the wrapper for the connection's next handle only after a clean handle whose core
            /// is closed and ownership released; after any error, or once the connection is disposed
            /// or collected, dispose it.
            /// </summary>
            private void ReturnOrDispose()
            {
                var alive = _cacheOwner.TryGetTarget(out var owner);
                var refusal = _error != null ? "error" : _coreFailed ? "core-failure" : null;
                if (refusal == null && alive) this.Cleanup(() => refusal = _child.ResetTransactionChild());
                if (_error != null) refusal = "error";
                // A refused return (disposed or occupied) counts its own reason.
                if (refusal == null && alive && owner.ReturnTransactionChild(_child)) return;
                if (!alive) CountOrphanedChild();
                else if (refusal != null) owner.CountChildDiscard(refusal);
                this.Cleanup(_child.Dispose);
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
