#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // A facade owns one reference; each actual escaping result owns its record.
    internal sealed class SharedMemoryReaders : ISharedReaderRegistry
    {
        private SharedMemoryReaderTable _table;
        internal SharedMemoryReaderTable Table => _table;
        internal SharedMemoryReaders(SharedMemoryReaderTable table) { table.AttachFacade(); _table = table; }
        public IDisposable Register(int version) => (_table ?? throw new ObjectDisposedException(nameof(SharedMemoryReaders))).Register(version);
        public IDisposable RegisterUnscanned(int version) => this.Register(version);
        public void Probe(int version) { if (_table == null) throw new ObjectDisposedException(nameof(SharedMemoryReaders)); }
        public int[] LiveVersions() => _table?.LiveVersions();
        public int? OldestVersion()
        {
            var versions = this.LiveVersions();
            return versions == null ? 0 : versions.Length == 0 ? (int?)null : versions.Min();
        }
        ~SharedMemoryReaders() { this.Dispose(); }
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Interlocked.Exchange(ref _table, null)?.DetachFacade();
        }
    }
}
#endif
