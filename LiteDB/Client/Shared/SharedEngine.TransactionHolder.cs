using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        /// <summary>One internal native owner per handle, independent of application threads.</summary>
        private sealed class TransactionHolder
        {
            private readonly SharedEngine _child;
            private readonly SemaphoreSlim _gate;
            private readonly SharedWaitDeadline _deadline;
            // Created once ownership is acquired, so held/idle durations exclude the admission wait.
            private SharedHandleActivity _activity;
            private readonly ManualResetEventSlim _opened = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _close = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim();
            private Exception _error;
            private LiteEngine _engine;

            internal TransactionHolder(SharedEngine child, SemaphoreSlim gate, SharedWaitDeadline deadline)
            {
                _child = child;
                _gate = gate;
                _deadline = deadline;
            }

            /// <summary>The recovery report of the handle's core, once it opened.</summary>
            internal WalRecoveryReport RecoveryReport => _child._recoveryReport;

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
                    if (acquired) this.Cleanup(() => _child.CloseDatabase());
                    if (_activity != null) SharedHandleRegistry.Unregister(_child._mutexName, _activity);
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
