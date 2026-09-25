#if NET8_0_OR_GREATER
using System;
using System.Threading;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Coordinator ownership. A named mutex belongs to the thread that acquired it,
    /// so a dedicated thread owns it until <see cref="Dispose"/>; any thread may release.
    /// Process death abandons it, which lets the next process take over.
    /// </summary>
    internal sealed class CoordinatorMutex : IDisposable
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly Thread _owner;
        private int _disposed;
        private volatile bool _abandon;

        private CoordinatorMutex(Thread owner) => _owner = owner;

        /// <summary>The previous coordinator died holding the mutex (its process or thread ended).</summary>
        internal bool TookOverAbandoned { get; private set; }

        /// <summary>Acquire without waiting; null when another process coordinates.</summary>
        internal static CoordinatorMutex TryAcquire(string name)
        {
            var acquired = false;
            var abandoned = false;
            Exception failure = null;
            var ready = new ManualResetEventSlim(false);
            CoordinatorMutex result = null;
            var thread = new Thread(() =>
            {
                Mutex mutex = null;
                try
                {
                    mutex = SharedMutexFactory.Create(name);
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = abandoned = true; }
                }
                catch (Exception ex) { failure = ex; }
                ready.Set();
                if (!acquired)
                {
                    mutex?.Dispose();
                    return;
                }
                result._release.Wait();
                // A simulated crash ends the owning thread without releasing: the OS then
                // reports the mutex abandoned, exactly as after process death.
                if (result._abandon) return;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            })
            { IsBackground = true, Name = "LiteDB coordinator mutex" };
            result = new CoordinatorMutex(thread);
            thread.Start();
            ready.Wait();
            ready.Dispose();
            if (failure != null) throw failure;
            if (acquired)
            {
                result.TookOverAbandoned = abandoned;
                return result;
            }
            thread.Join();
            result._release.Dispose();
            return null;
        }

        /// <summary>Test crash: end ownership without releasing, as a killed process would.</summary>
        internal void Abandon()
        {
            _abandon = true;
            this.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _owner.Join();
        }
    }
}
#endif
