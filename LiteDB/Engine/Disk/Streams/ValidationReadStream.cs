using System;
using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// Owns a read-only inspection handle. CryptoStream initialization and disposal
    /// may request flushes; inspection must neither publish bytes nor add barriers.
    /// </summary>
    internal sealed class ValidationReadStream : Stream
    {
        private readonly Stream _stream;
        internal ValidationReadStream(Stream stream) => _stream = stream;
        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}
