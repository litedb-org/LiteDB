using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement internal thread-safe Stream using lock control - A single instance of ConcurrentStream are not multi thread,
    /// but multiples ConcurrentStream instances using same stream base will support concurrency
    /// </summary>
    internal class ConcurrentStream : Stream
    {
        private readonly Stream _stream;
        private readonly bool _canWrite;
        private readonly bool _leaveOpen;

        private long _position = 0;

        public ConcurrentStream(Stream stream, bool canWrite, bool leaveOpen = false)
        {
            _stream = stream;
            _canWrite = canWrite;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _canWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _position; set => _position = value; }

        public override void Flush() => _stream.Flush();

        public override void SetLength(long value) => _stream.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen) _stream.Dispose();
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock(_stream)
            {
                var position =
                    origin == SeekOrigin.Begin ? offset :
                    origin == SeekOrigin.Current ? _position + offset :
                    _position - offset;

                _position = position;

                return _position;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // lock internal stream and set position before read
            lock (_stream)
            {
                _stream.Position = _position;
                var read = _stream.Read(buffer, offset, count);
                _position = _stream.Position;
                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_canWrite == false) throw new NotSupportedException("Current stream are readonly");

            // lock internal stream and set position before write
            lock (_stream)
            {
                _stream.Position = _position;
                _stream.Write(buffer, offset, count);
                _position = _stream.Position;
            }
        }
    }
}
