using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed class ChecksummedWalFactory : IStreamFactory
    {
        private readonly IStreamFactory _inner;
        private readonly WalChecksum _checksum;
        internal ChecksummedWalFactory(IStreamFactory inner, WalChecksum checksum)
        {
            _inner = inner;
            _checksum = checksum;
        }
        public string Name => _inner.Name;
        public bool CloseOnDispose => _inner.CloseOnDispose;
        public Stream GetStream(bool canWrite, bool sequential) => new ChecksummedWalStream(_inner.GetStream(canWrite, sequential), _checksum);
        private long ContentLength => System.Math.Max(0, _inner.GetLength() - _checksum.JournalBytes);
        public long GetLength() => _checksum.Enabled ? ContentLength / WalChecksum.FrameSize * PAGE_SIZE : ContentLength;
        internal long TrailingBytes => _checksum.Enabled ? WalPadding.TrailingBytes(ContentLength) : ContentLength % PAGE_SIZE;
        public bool Exists() => _inner.Exists();
        public bool IsLocked() => _inner.IsLocked();
        public void Delete() => _inner.Delete();
        public void TrimCapacity(Stream stream) => _inner.TrimCapacity(stream);
        internal void SyncDirectory() { if (_inner is FileStreamFactory file) file.SyncDirectory(); }
        /// <summary>The WAL is a file (not a caller stream), so its syncs go through <see cref="NativeFileSync"/>.</summary>
        internal bool IsFile => _inner is FileStreamFactory;
        public void Dispose() => _inner.Dispose();
    }
}
