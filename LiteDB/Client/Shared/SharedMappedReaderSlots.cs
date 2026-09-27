#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Ephemeral reader leases with atomic mapped publication. The exclusive lease
    /// handle proves liveness; persisted bytes are never proof of a living reader.
    /// Both publisher and scanner use mapped views (never mixed mapped/file reads).
    /// Older registries cannot parse the prefix and conservatively refuse reclamation.
    /// </summary>
    internal sealed unsafe class SharedMappedReaderSlots : IDisposable
    {
        internal const string Prefix = "mapped-";
        internal const int Size = 4096;
        private const long Magic = 0x31544F4C534D444C;
        private const int Capacity = Size / sizeof(long) - 1;
        private readonly object _gate = new object();
        private readonly FileStream _lease;
        private readonly SharedMappedReaderView _view;
        private readonly int[] _next = new int[Capacity];
        private int _free;
        private int _inUse;
        private bool _disposing;
        private bool _closed;

        private SharedMappedReaderSlots(FileStream lease, SharedMappedReaderView view)
        {
            _lease = lease;
            _view = view;
            for (var i = 0; i < Capacity; i++) _next[i] = i + 1;
            _next[Capacity - 1] = -1;
        }

        // Creation requires database ownership. Hot admission only reuses this table.
        internal static SharedMappedReaderSlots Create(string directory)
        {
            Directory.CreateDirectory(directory);
            var leasePath = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N") + ".lease");
            SharedMappedReaderView view = null;
            FileStream lease = null;
            try
            {
                view = SharedMappedReaderView.Create(SharedReaderSlots.ContentPath(leasePath));
                view.Store(0, Magic);
                lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return new SharedMappedReaderSlots(lease, view);
            }
            catch
            {
                lease?.Dispose();
                view?.Dispose();
                throw;
            }
        }

        internal IDisposable Lease(int version)
        {
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
            lock (_gate)
            {
                if (_disposing) throw new ObjectDisposedException(nameof(SharedMappedReaderSlots));
                if (_free < 0) throw new IOException("The mapped reader slot limit was reached.");
                var index = _free;
                var result = new Slot(this, index);
                _view.Store(index + 1, (long)(uint)version | (long)(uint)~version << 32);
                _free = _next[index];
                _inUse++;
                return result;
            }
        }

        private void Release(int index)
        {
            lock (_gate)
            {
                _view.Store(index + 1, 0);
                _next[index] = _free;
                _free = index;
                _inUse--;
                this.CloseIfDone();
            }
        }

        internal static int[] ReadVersions(string leasePath)
        {
            try
            {
                using var view = SharedMappedReaderView.Open(SharedReaderSlots.ContentPath(leasePath));
                if (view.Load(0) != Magic) return null;
                var versions = new List<int>();
                for (var i = 1; i <= Capacity; i++)
                {
                    var value = view.Load(i);
                    if (value == 0) continue;
                    var version = (int)value;
                    if (version < 0 || (int)(value >> 32) != ~version) return null;
                    versions.Add(version);
                }
                return versions.ToArray();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposing = true;
                this.CloseIfDone();
            }
        }

        private void CloseIfDone()
        {
            if (!_disposing || _inUse != 0 || _closed) return;
            _closed = true;
            try { _lease.Dispose(); }
            finally { _view.Dispose(); }
        }

        private sealed class Slot : IDisposable
        {
            private SharedMappedReaderSlots _owner;
            private readonly int _index;
            internal Slot(SharedMappedReaderSlots owner, int index) { _owner = owner; _index = index; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_index);
        }
    }
}
#endif
