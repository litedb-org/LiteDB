using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB
{
    public partial class SharedEngine
    {
        /// <summary>How long Dispose waits for admitted calls of other threads to return.</summary>
        internal static readonly TimeSpan DisposeCallWait = TimeSpan.FromSeconds(10);

        // Calls admitted to the engine (they own the mutex and passed the disposed check)
        // that have not returned yet, per thread. Guarded by _useLock.
        private readonly Dictionary<int, int> _admitted = new Dictionary<int, int>();
        private int _admittedCalls;
        // Every connection to one database shares its native mutex, and so this name.
        private readonly string _mutexName;
        private Func<bool> _callRetains;

        /// <summary>
        /// Under _useLock, with the mutex owned: refuse a call once Dispose started, else count
        /// it. Dispose closes the engine only after every counted call of another thread
        /// returned. An engine closed under a live call can neither dispose its busy page
        /// cache (the call's pinned or writable pages leak) nor finish the call's transaction.
        /// </summary>
        private void AdmitLocked()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                Reachability.Sometimes("refusal:shared-call-after-dispose");
                throw new ObjectDisposedException(nameof(SharedEngine));
            }
            var thread = Environment.CurrentManagedThreadId;
            _admitted.TryGetValue(thread, out var depth);
            _admitted[thread] = depth + 1;
            _admittedCalls++;
        }

        private int AdmittedDepth()
        {
            lock (_useLock) return _admitted.TryGetValue(Environment.CurrentManagedThreadId, out var depth) ? depth : 0;
        }

        // User callbacks and custom streams may open readers that escape the call.
        private bool CanScope => _settings.ReadTransform == null && _settings.DataStream == null &&
            _settings.LogStream == null && _settings.TempStream == null;

        private T QueryDatabase<T>(Func<T> Query) => this.Call(() =>
        {
            var use = OpenDatabase(scoped: this.CanScope);
            try
            {
                return Query();
            }
            finally
            {
                CloseDatabase(use);
            }
        });

        /// <summary>Run a public call; the admission it made, if any, ends when it returns.</summary>
        private T Call<T>(Func<T> call)
        {
            this.ThrowIfTeardownReentry();
            var depth = this.AdmittedDepth();
            var frame = this.OwnershipFrame(this.CallRetains);
            try
            {
                return call();
            }
            finally
            {
                frame.Dispose();
                this.EndAdmissions(depth);
            }
        }

        private Func<bool> CallRetains => _callRetains ?? (_callRetains = this.RetainsOwnershipOnCurrentThread);

        /// <summary>
        /// Whether a call of this connection executing on the current thread keeps the native
        /// mutex: its ownership belongs to this thread, or this thread's operation is inside
        /// the pin. Either ends only after that call, and any callback it runs, returns.
        /// </summary>
        private bool RetainsOwnershipOnCurrentThread()
        {
            if (_owner.IsOwnedByCurrentThread) return true;
            var pin = _pin;
            return pin != null && pin.IsOperatingOn(Thread.CurrentThread);
        }

        // A holder thread owns the OS mutex until the close it runs has returned.
        private static readonly Func<bool> HolderRetains = () => true;

        /// <summary>
        /// Frame for work outside a public call that can run user code (a caller stream while
        /// an engine closes) under the mutex; <paramref name="retains"/> tells whether it still holds it.
        /// </summary>
        private SharedCallFrames.Scope OwnershipFrame(Func<bool> retains) =>
            SharedCallFrames.Enter(_mutexName, this, retains);

        /// <summary>
        /// A reader streaming under the ownership of <paramref name="use"/>, or else of the
        /// connection's ownership <paramref name="generation"/>, which it keeps until disposed.
        /// </summary>
        private SharedDataReader RetainingReader(IBsonDataReader reader, Action dispose, SharedMutexPin use, int generation)
        {
            Func<bool> retains = use != null
                ? () => ReferenceEquals(_pin, use)
                : (Func<bool>)(() => _owner.Generation == generation);
            return new SharedDataReader(reader, dispose, _mutexName, this, retains);
        }

        /// <summary>
        /// Before any blocking acquisition of the native mutex: refuse when a call or reader of
        /// another connection to this database retains the mutex on this thread, for example
        /// when its input sequence or ReadTransform callback calls this connection. The wait
        /// could never end, because that ownership is released only after the callback returns.
        /// An idle owner (a reader, pin or transaction between calls) is not in a frame and is
        /// still waited for: a pin ends for the waiter and a result may be disposed on any thread.
        /// An explicit transaction completes only on its own thread (#3073).
        /// </summary>
        private void ThrowIfCallerRetainsOwnership()
        {
            if (!SharedCallFrames.RetainedByOther(_mutexName, this)) return;
            Reachability.Sometimes("refusal:shared-peer-waits-for-own-ownership");
            throw new InvalidOperationException(
                "Cannot wait for shared-mode ownership of this database from inside an operation of another " +
                "connection to it that holds the ownership on this thread, such as its input sequence or " +
                "ReadTransform callback. Use that connection for nested operations, or run them after it returns.");
        }

        private void ThrowIfTeardownReentry()
        {
            if (!SharedCallFrames.IsTearingDown(this)) return;
            if (Volatile.Read(ref _disposed) != 0)
            {
                Reachability.Sometimes("refusal:shared-teardown-reentry-after-dispose");
                throw new ObjectDisposedException(nameof(SharedEngine));
            }
            Reachability.Sometimes("refusal:shared-teardown-reentry");
            throw new InvalidOperationException("Cannot reenter a shared connection from inside its executing core teardown.");
        }

        /// <summary>Keep writer ownership until actual teardown returns, including caller-stream callbacks.</summary>
        private List<Exception> CloseRetainedCore(LiteEngine core, bool checkpoint = true, bool final = false)
        {
            using (SharedCallFrames.Enter(_mutexName, this, HolderRetains, teardown: true))
                return core.Close(checkpoint: checkpoint, final: final);
        }

        private void EndAdmissions(int depth)
        {
            var thread = Environment.CurrentManagedThreadId;
            lock (_useLock)
            {
                if (!_admitted.TryGetValue(thread, out var current) || current <= depth) return;
                _admittedCalls -= current - depth;
                if (depth == 0) _admitted.Remove(thread);
                else _admitted[thread] = depth;
                Monitor.PulseAll(_useLock);
            }
        }

        /// <summary>
        /// Dispose's drain: wait until no other thread's admitted call is running. Calls of the
        /// disposing thread itself (Dispose from inside an operation) are not waited for. The
        /// wait is bounded: a call blocked on something only this Dispose would end (another
        /// thread's explicit transaction holding an engine lock) must not hang it; past the
        /// bound Dispose proceeds, and that call fails on the closed engine.
        /// </summary>
        private void WaitForAdmittedCalls()
        {
            var waited = Stopwatch.StartNew();
            var thread = Environment.CurrentManagedThreadId;
            lock (_useLock)
            {
                while (true)
                {
                    _admitted.TryGetValue(thread, out var own);
                    if (_admittedCalls - own <= 0) return;
                    Reachability.Sometimes("maintenance:shared-dispose-during-active-call");
                    var remaining = DisposeCallWait - waited.Elapsed;
                    if (remaining <= TimeSpan.Zero) return;
                    Monitor.Wait(_useLock, remaining);
                }
            }
        }
    }
}
