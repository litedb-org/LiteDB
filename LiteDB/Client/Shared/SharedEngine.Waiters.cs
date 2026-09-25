using System.Threading;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Threads of this instance blocked on the mutex. A pin ends for them and no new
        // pin starts: a one-shot release request is lost when the owner re-pins first.
        private int _mutexWaiters;
        private readonly object _waitersLock = new object();

        /// <summary>
        /// Enter the connection's mutex ownership, counted as a waiter meanwhile so that
        /// any pin of this instance, including one started after this call, ends for it.
        /// </summary>
        private bool EnterOwner()
        {
            if (_owner.IsOwnedByCurrentThread) return _owner.Enter();
            this.AddMutexWaiter();
            try
            {
                return _owner.Enter();
            }
            finally
            {
                this.RemoveMutexWaiter();
            }
        }

        private bool HasMutexWaiters() => Volatile.Read(ref _mutexWaiters) > 0;

        private void AddMutexWaiter()
        {
            lock (_waitersLock) _mutexWaiters++;
        }

        private void RemoveMutexWaiter()
        {
            lock (_waitersLock)
            {
                if (--_mutexWaiters == 0) Monitor.PulseAll(_waitersLock);
            }
        }

        /// <summary>Block until no thread of this instance waits for the mutex.</summary>
        private void WaitForMutexWaiters()
        {
            lock (_waitersLock)
            {
                while (_mutexWaiters > 0) Monitor.Wait(_waitersLock);
            }
        }
    }
}
