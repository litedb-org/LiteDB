using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Reusable threads for Shared transaction-handle holders. A job owns native writer ownership
    /// only while it runs: it takes and releases the mutex itself. An idle thread retains no job,
    /// owner, engine or application execution context. At most <see cref="MaximumIdle"/> threads
    /// stay idle, each for at most one second; busy threads are never capped, so a holder of one
    /// database never waits for a holder of another.
    /// </summary>
    internal static class SharedHolderScheduler
    {
        private static readonly object Gate = new object();
        private static readonly List<Worker> Idle = new List<Worker>();
        internal const int MaximumIdle = 2;
        internal const int IdleMilliseconds = 1000;
#if DEBUG || TESTING
        // Test observers and controls; counters are process-wide, read them as deltas.
        internal static int Created, Reused, Expired, DirtyExits;
        internal static int? IdleWaitOverride;
        internal static Action<Thread> BeforeIdleExpiry;

        internal static bool IsIdle(Thread thread)
        {
            lock (Gate) return Idle.Exists(worker => worker.Thread == thread);
        }

        internal static int IdleCount
        {
            get { lock (Gate) return Idle.Count; }
        }

        /// <summary>End every idle worker now, so a test starts from an empty pool.</summary>
        internal static void RetireIdleWorkers()
        {
            lock (Gate)
            {
                foreach (var worker in Idle) worker.Retired = true;
                Idle.Clear();
                Monitor.PulseAll(Gate);
            }
        }
#endif

        /// <summary>
        /// Run <paramref name="action"/> on an idle worker, or on a new one. The caller's execution
        /// context never flows: every job runs in a copy of the worker's own clean context.
        /// </summary>
        internal static Thread Queue(Action action)
        {
            lock (Gate)
            {
                if (Idle.Count != 0)
                {
                    var worker = Idle[Idle.Count - 1];
                    Idle.RemoveAt(Idle.Count - 1);
                    worker.Pending = action;
#if DEBUG || TESTING
                    Reused++;
#endif
                    Monitor.PulseAll(Gate);
                    return worker.Thread;
                }
                // Busy workers never limit another database's writer progress.
                var created = new Worker(action);
                // The thread's initial context must not be the caller's: its AsyncLocals could root
                // an abandoned facade, and so the writer ownership its finalizer would release.
                if (ExecutionContext.IsFlowSuppressed()) created.Thread.Start();
                else using (ExecutionContext.SuppressFlow()) created.Thread.Start();
#if DEBUG || TESTING
                Created++;
#endif
                return created.Thread;
            }
        }

        /// <summary>
        /// Whether a finished job left nothing on this thread. A leaked scoped mutex ownership can
        /// only be released by its thread; such a thread exits, so the OS abandons the mutex.
        /// </summary>
        private static bool IsClean() =>
            SharedMutexScope.CanEnter && SharedCallFrames.IsEmpty && !SharedWaitDeadline.IsInherited &&
            TransactionContext.IsThreadClear;

        private sealed class Worker
        {
            internal readonly Thread Thread;
            internal Action Pending;
#if DEBUG || TESTING
            internal bool Retired;
#endif
            private ExecutionContext _cleanContext;

            internal Worker(Action action)
            {
                Pending = action;
                Thread = new Thread(this.Run) { IsBackground = true, Name = "LiteDB shared transaction holder" };
            }

            private void Run()
            {
                // Thread startup suppressed the caller's flow. Each job still runs inside a fresh
                // copy, so its AsyncLocal or culture changes cannot reach the next job.
                _cleanContext = ExecutionContext.Capture();
                try
                {
                    while (true)
                    {
                        this.ExecuteOne();
                        if (!IsClean())
                        {
#if DEBUG || TESTING
                            Interlocked.Increment(ref DirtyExits);
#endif
                            return;
                        }
                        if (!this.WaitIdle()) return;
                    }
                }
                finally { _cleanContext?.Dispose(); }
            }

            /// <summary>Wait in the pool for the next job; false when this worker should exit.</summary>
            private bool WaitIdle()
            {
                lock (Gate)
                {
                    if (Idle.Count >= MaximumIdle) return false;
                    Idle.Add(this);
                    var idleSince = Stopwatch.GetTimestamp();
                    var timeout = IdleMilliseconds;
#if DEBUG || TESTING
                    timeout = IdleWaitOverride ?? timeout;
#endif
                    while (Pending == null)
                    {
#if DEBUG || TESTING
                        if (Retired) return false;
#endif
                        var remaining = timeout - (Stopwatch.GetTimestamp() - idleSince) * 1000d / Stopwatch.Frequency;
                        if (remaining <= 0)
                        {
#if DEBUG || TESTING
                            BeforeIdleExpiry?.Invoke(Thread);
                            Expired++;
#endif
                            Idle.Remove(this);
                            return false;
                        }
                        Monitor.Wait(Gate, TimeSpan.FromMilliseconds(remaining));
                    }
                    return true;
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void ExecuteOne()
            {
                Action action;
                lock (Gate)
                {
                    action = Pending;
                    Pending = null;
                }
                // This entire frame returns before idle publication: neither the worker's fields
                // nor a JIT temporary may retain a completed job and the holder it captured.
                using (var context = _cleanContext?.CreateCopy())
                {
                    if (context == null) action();
                    else ExecutionContext.Run(context, state => ((Action)state)(), action);
                }
            }
        }
    }
}
