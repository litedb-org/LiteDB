using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// A second named mutex that every blocking acquisition of the shared mutex passes
    /// while it waits. Named mutexes are not fair: a party that just released the mutex
    /// (an ending pin that restarts, a tight loop of operations) can take it again
    /// before a waiter that was woken gets to run. A waiter holds the turnstile until it
    /// owns the mutex, so the releasing party blocks at the turnstile and the waiter goes
    /// first. The turnstile also tells a holder, in any process, that someone is waiting.
    /// Releases never touch it, so no holder of the shared mutex ever waits for it while
    /// a waiter waits for the holder. Versions without a turnstile still interoperate:
    /// they only compete for the shared mutex as before.
    /// </summary>
    internal sealed class SharedMutexTurnstile
    {
        private readonly Mutex _turn;

        public SharedMutexTurnstile(Mutex turn)
        {
            _turn = turn;
        }

        /// <summary>
        /// Block until <paramref name="mutex"/> is owned, queued at the turnstile.
        /// Throws <see cref="AbandonedMutexException"/> as <see cref="WaitHandle.WaitOne()"/> does.
        /// </summary>
        public void Wait(Mutex mutex)
        {
            var queued = this.Enter();
            try
            {
                mutex.WaitOne();
            }
            finally
            {
                if (queued) _turn.ReleaseMutex();
            }
        }

        /// <summary>True when another participant is queued for the shared mutex.</summary>
        public bool HasWaiter()
        {
            try
            {
                if (!_turn.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                // A waiter died while queued; this thread now owns the turnstile.
            }
            _turn.ReleaseMutex();
            return false;
        }

        private bool Enter()
        {
            try
            {
                return _turn.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // It guards no state: a waiter that died while queued only gave up its turn.
                return true;
            }
        }
    }
}
