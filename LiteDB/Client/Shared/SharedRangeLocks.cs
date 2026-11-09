using System;
using System.IO;

namespace LiteDB.Client.Shared
{
    // One file, three independently OPENED descriptions. Never dup an owner to
    // probe it: OFD_GETLK deliberately ignores locks on its own description.
    internal sealed class SharedRangeLocks : IDisposable
    {
        private readonly DatabaseFileLock _owner;
        private readonly DatabaseFileLock _probe;
        private readonly DatabaseFileLock _writer;
        private bool _writing, _sharedDms;
#if DEBUG || TESTING
        [ThreadStatic] internal static Action WriteRetry;
#endif
#if DEBUG || TESTING
        internal long WriteAcquisitions { get; private set; }
#endif
        internal string Identity => _owner.Identity;

        internal SharedRangeLocks(string path)
        {
            try
            {
                _owner = new DatabaseFileLock(path, false, true);
                _probe = new DatabaseFileLock(path, false, false);
                _writer = new DatabaseFileLock(path, false, false);
                if (_owner.Identity != _probe.Identity || _owner.Identity != _writer.Identity ||
                    DatabaseFileIdentity.CanonicalPath(path) != Path.GetFullPath(path))
                    throw new IOException("Shared coordination pathname changed or is an alias.");
            }
            catch { this.Dispose(); throw; }
        }

        internal bool ExclusiveDms { get; private set; }
        internal bool TryExclusiveDms()
        {
#if PROFILE
            using var profile = SharedOpenProfile.Measure("SharedRangeLocks.TryExclusiveDms");
#endif

            if (ExclusiveDms) return true;
            // Every production conversion owns bootstrap. Windows cannot upgrade
            // a shared LockFileEx range in place: drop only our shared layer while
            // bootstrap excludes new attach/reset/recovery, then restore it on
            // contention or error. Existing peers keep their shared locks.
            var restore = _sharedDms && DatabaseFileIdentity.Windows;
            if (restore) _owner.Unlock(SharedMemoryLayout.Dms);
            var acquired = false;
            try
            {
                acquired = _owner.TryLock(SharedMemoryLayout.Dms, true);
                if (!acquired) return false;
                _sharedDms = false;
                ExclusiveDms = true;
                return true;
            }
            finally { if (restore && !acquired) _owner.Lock(SharedMemoryLayout.Dms, false); }
        }
        internal void ShareDms() { _owner.Lock(SharedMemoryLayout.Dms, false); ExclusiveDms = false; _sharedDms = true; }
        internal void DowngradeDms() { _owner.Downgrade(SharedMemoryLayout.Dms); ExclusiveDms = false; _sharedDms = true; }
        internal bool TryClaim(int slot) => _owner.TryLock(Slot(slot), true);
        internal void ReleaseSlot(int slot) => _owner.Unlock(Slot(slot));
        internal bool IsLive(int slot) => _probe.Conflicts(Slot(slot));

        internal void EnterWrite() => EnterWrite(false);

        internal void EnterWrite(bool abandonedOwner)
        {
#if PROFILE
            using var profile = SharedOpenProfile.Measure("SharedRangeLocks.EnterWrite");
#endif

            if (_writing) throw new InvalidOperationException("WRITE is already owned.");
            if (!_writer.TryLock(SharedMemoryLayout.Write, true))
            {
#if DEBUG || TESTING
                LiteDB.Utils.Reachability.FaultPoint("WriteRetry");
                WriteRetry?.Invoke();
#endif
                // A killed process can abandon its named mutex before the kernel
                // finishes closing its OFD descriptions. Keep main ownership and
                // require the actual WRITE lock; never infer its release from death.
                if (!abandonedOwner || !System.Threading.SpinWait.SpinUntil(
                    () => _writer.TryLock(SharedMemoryLayout.Write, true), TimeSpan.FromSeconds(5)))
                    throw new IOException("Shared main mutex / WRITE ownership mismatch.");
            }
            _writing = true;
#if DEBUG || TESTING
            WriteAcquisitions++;
#endif
        }

        internal void ExitWrite()
        {
            if (!_writing) return;
            _writer.Unlock(SharedMemoryLayout.Write);
            _writing = false;
        }

        private static long Slot(int slot)
        {
            if (slot < 0 || slot >= 65536) throw new ArgumentOutOfRangeException(nameof(slot));
            return SharedMemoryLayout.SlotLocks + slot;
        }

        public void Dispose()
        {
            // Closing descriptions releases even on another thread or after failure.
            _writer?.Dispose();
            _probe?.Dispose();
            _owner?.Dispose();
        }
    }
}
