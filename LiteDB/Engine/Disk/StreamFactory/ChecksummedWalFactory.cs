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
        internal long TrailingBytes => ContentLength % (_checksum.Enabled ? WalChecksum.FrameSize : PAGE_SIZE);
        public bool Exists() => _inner.Exists();
        public bool IsLocked() => _inner.IsLocked();
        public void Delete() => _inner.Delete();
        public void TrimCapacity(Stream stream) => _inner.TrimCapacity(stream);
        public void Dispose() => _inner.Dispose();
    }
}
