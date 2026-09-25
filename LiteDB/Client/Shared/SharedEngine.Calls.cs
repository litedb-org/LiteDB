using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

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

        /// <summary>
        /// Under _useLock, with the mutex owned: refuse a call once Dispose started, else count
        /// it. Dispose closes the engine only after every counted call of another thread
        /// returned. An engine closed under a live call can neither dispose its busy page
        /// cache (the call's pinned or writable pages leak) nor finish the call's transaction.
        /// </summary>
        private void AdmitLocked()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SharedEngine));
            var thread = Environment.CurrentManagedThreadId;
            _admitted.TryGetValue(thread, out var depth);
            _admitted[thread] = depth + 1;
            _admittedCalls++;
        }

        private int AdmittedDepth()
        {
            lock (_useLock) return _admitted.TryGetValue(Environment.CurrentManagedThreadId, out var depth) ? depth : 0;
        }

        private T QueryDatabase<T>(Func<T> Query) => this.Call(() =>
        {
            var use = OpenDatabase();
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
            var depth = this.AdmittedDepth();
            try
            {
                return call();
            }
            finally
            {
                this.EndAdmissions(depth);
            }
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
                    var remaining = DisposeCallWait - waited.Elapsed;
                    if (remaining <= TimeSpan.Zero) return;
                    Monitor.Wait(_useLock, remaining);
                }
            }
        }
    }
}
