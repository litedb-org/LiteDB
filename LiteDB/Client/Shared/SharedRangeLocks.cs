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
        private bool _writing;
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

        internal bool TryExclusiveDms() => _owner.TryLock(SharedMemoryLayout.Dms, true);
        internal void ShareDms() => _owner.Lock(SharedMemoryLayout.Dms, false);
        internal void DowngradeDms() => _owner.Downgrade(SharedMemoryLayout.Dms);
        internal bool TryClaim(int slot) => _owner.TryLock(Slot(slot), true);
        internal void ReleaseSlot(int slot) => _owner.Unlock(Slot(slot));
        internal bool IsLive(int slot) => _probe.Conflicts(Slot(slot));

        internal void EnterWrite()
        {
            if (_writing) throw new InvalidOperationException("WRITE is already owned.");
            if (!_writer.TryLock(SharedMemoryLayout.Write, true))
                throw new IOException("Shared main mutex / WRITE ownership mismatch.");
            _writing = true;
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
