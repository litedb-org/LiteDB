using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// Borrows a stream without owning its lifetime or maintaining a separate
    /// position. Used where .NET Standard 2.0 wrappers cannot leave a stream open.
    /// </summary>
    internal sealed class NonClosingStream : Stream
    {
        private readonly Stream _stream;

        public NonClosingStream(Stream stream) => _stream = stream;

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override void Flush() => _stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);
    }
}
