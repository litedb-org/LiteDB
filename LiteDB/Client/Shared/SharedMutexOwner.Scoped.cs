using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Scoped ownerships: the caller ends them on its own thread before it returns, so the
    /// OS mutex is taken and released on that thread, without the holder's two handoffs.
    /// </summary>
    internal sealed partial class SharedMutexOwner
    {
        // The thread that owns the OS mutex itself for a scoped ownership, or null.
        private Thread _direct;
        // Scoped ownerships of any connection on this thread. A nested fresh one takes
        // the holder path: this thread already owns the named mutex, so a direct wait
        // would succeed recursively and let two connections in at once.
        [ThreadStatic] private static int _directOnThread;

        /// <summary>
        /// True while the calling thread owns the OS mutex itself: the current ownership
        /// is scoped and must end on this thread before the caller returns.
        /// </summary>
        public bool OwnsDirectly
        {
            get { lock (_sync) return _owner != null && ReferenceEquals(_direct, Thread.CurrentThread); }
        }

        /// <summary>
        /// Acquire the OS mutex on the calling thread itself, which holds the gate. Only a
        /// scoped ownership may: nobody but this thread can release it.
        /// </summary>
        private bool TakeDirect(bool block, out bool abandoned)
        {
            abandoned = false;
            bool acquired;
            try
            {
                if (block) { _turnstile.Wait(_mutex); acquired = true; }
                else acquired = _turnstile.TryWait(_mutex);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                abandoned = true;
            }
            catch
            {
                _gate.Release();
                throw;
            }
            if (!acquired)
            {
                _gate.Release();
                return false;
            }
            _directOnThread++;
            lock (_sync)
            {
                _owner = Thread.CurrentThread;
                _direct = _owner;
                _recursion = 1;
            }
            return true;
        }

        /// <summary>Release a scoped ownership's OS mutex; Exit checked that this is its thread.</summary>
        private void ReleaseDirect()
        {
            try { _mutex.ReleaseMutex(); }
            finally
            {
                _directOnThread--;
                _gate.Release();
            }
        }
    }
}
