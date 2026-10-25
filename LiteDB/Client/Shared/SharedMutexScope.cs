using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Native ownership for a call whose entire lifetime stays on the calling thread.
    /// A nested connection uses the holder: recursively entering the same native mutex
    /// through a second connection would otherwise admit two independent engines.
    /// </summary>
    internal sealed class SharedMutexScope
    {
        [ThreadStatic] private static int _onThread;
        private readonly Mutex _mutex;
        private readonly SharedMutexTurnstile _turnstile;

        // Guarded by SharedMutexOwner's synchronization lock.
        internal Thread Owner;
        internal static bool CanEnter => _onThread == 0;

        internal SharedMutexScope(Mutex mutex, SharedMutexTurnstile turnstile)
        {
            _mutex = mutex;
            _turnstile = turnstile;
        }

        internal bool Take(bool block, out bool abandoned)
        {
            abandoned = false;
            try
            {
                if (block) _turnstile.Wait(_mutex);
                else if (!_turnstile.TryWait(_mutex)) return false;
            }
            catch (AbandonedMutexException) { abandoned = true; }
            _onThread++;
            return true;
        }

        internal void Release()
        {
            try { _mutex.ReleaseMutex(); }
            finally { _onThread--; }
        }
    }
}
