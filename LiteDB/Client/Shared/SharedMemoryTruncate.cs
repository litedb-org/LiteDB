#if NET8_0_OR_GREATER
using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Called under main/WRITE. Bootstrap is nonblocking: an attaching process
    // may already own it while waiting for main. Keep partial checkpoint behavior
    // on contention and leave the WAL incarnation intact.
    internal sealed class SharedMemoryTruncate : IDisposable
    {
        private readonly SharedMemoryAuthority _authority;
        private readonly Mutex _bootstrap;
        private bool _held, _converted;

        private SharedMemoryTruncate(SharedMemoryAuthority authority, string filename)
        {
            _authority = authority;
            _bootstrap = SharedMutexFactory.Create(SharedMutexNameFactory.CreateUsingSha1(
                SharedMemoryAuthority.PathFor(filename)) + ".Bootstrap");
        }

        internal static IDisposable TryEnter(SharedMemoryAuthority authority, string filename)
        {
            var lease = new SharedMemoryTruncate(authority, filename);
            try
            {
                try { lease._held = lease._bootstrap.WaitOne(0); }
                catch (AbandonedMutexException) { lease._held = true; }
                if (!lease._held) { lease.Dispose(); return null; }
                // Bootstrap initialization, maintenance and final close can already
                // own exclusive DMS. Borrow it without downgrading their outer scope.
                if (!authority.Locks.ExclusiveDms)
                {
                    if (!authority.Locks.TryExclusiveDms()) { lease.Dispose(); return null; }
                    lease._converted = true;
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        public void Dispose()
        {
            try { if (_converted) { _authority.Locks.DowngradeDms(); _converted = false; } }
            finally
            {
                try { if (_held) { _bootstrap.ReleaseMutex(); _held = false; } }
                finally { _bootstrap.Dispose(); }
            }
        }
    }
}
#endif
