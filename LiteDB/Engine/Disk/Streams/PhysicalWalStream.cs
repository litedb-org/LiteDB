using System;
using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// Physical EOF for an exclusively engine-owned, exact FileStream writer.
    /// This is neither the logical frame length nor fallocate's reserved capacity.
    /// All physical mutations, including journals, must pass through this stream.
    /// The writer monitor serializes access; descriptor-only group sync does not mutate EOF.
    /// </summary>
    internal sealed class PhysicalWalStream : Stream
    {
        private readonly FileStream _file;
        private long? _length;

        internal PhysicalWalStream(FileStream file)
        {
            _file = file;
            _length = file.Length;
        }

        public override bool CanRead => _file.CanRead;
        public override bool CanSeek => _file.CanSeek;
        public override bool CanWrite => _file.CanWrite;
        public override long Length => (_length ?? (_length = _file.Length)).Value;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Forget EOF before I/O: an exception can follow a partial write.
            var length = Length;
            var position = Position;
            _length = null;
            _file.Write(buffer, offset, count);
            _length = count == 0 ? length : Math.Max(length, checked(position + count));
        }

        public override void SetLength(long value)
        {
            _length = null;
            _file.SetLength(value);
            _length = value;
        }

        public override void Flush()
        {
            try { _file.Flush(); }
            catch { _length = null; throw; }
        }

        internal void FlushToDisk()
        {
            try { _file.FlushToDisk(); }
            catch { _length = null; throw; }
        }
    }
}
