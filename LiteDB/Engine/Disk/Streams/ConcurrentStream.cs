using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement internal thread-safe Stream using lock control - A single instance of ConcurrentStream are not multi thread,
    /// but multiples ConcurrentStream instances using same stream base will support concurrency.
    /// The wrappers of a base stream that may hold a write after it returned (any but a MemoryStream)
    /// share its <see cref="HeldWrites"/>: an access by another wrapper (a reader's seek) first
    /// flushes what the stream holds for the writer, and a failure there is handed to the writer.
    /// </summary>
    internal class ConcurrentStream : Stream
    {
        private readonly Stream _stream;
        private readonly bool _canWrite;
        private readonly bool _leaveOpen;
        private readonly HeldWrites _held;

        private long _position = 0;

        public ConcurrentStream(Stream stream, bool canWrite, bool leaveOpen, HeldWrites held)
        {
            _stream = stream;
            _canWrite = canWrite;
            _leaveOpen = leaveOpen;
            _held = held;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _canWrite;

        public override long Length
        {
            get
            {
                lock (_stream)
                {
                    _held?.WriteOn(_stream, this);
                    return _stream.Length;
                }
            }
        }

        public override long Position { get => _position; set => _position = value; }

        public override void Flush()
        {
            lock (_stream)
            {
                this.BeforeWrite();
                try { _stream.Flush(); }
                catch { _held?.Heard(); throw; }
                _held?.Flushed();
            }
        }

        internal void FlushToDisk()
        {
            lock (_stream)
            {
                this.BeforeWrite();
                try { _stream.FlushToDisk(); }
                catch { _held?.Heard(); throw; }
                _held?.Flushed();
            }
        }

        public override void SetLength(long value)
        {
            // WAL rollback can truncate while another wrapper is reading.
            lock (_stream)
            {
                this.BeforeWrite();
                try { _stream.SetLength(value); }
                catch { _held?.Heard(); throw; }
            }
        }

        /// <summary>Before a write, flush or truncation: hear that a held write of this wrapper was torn, and write on another's.</summary>
        private void BeforeWrite()
        {
            if (_held == null) return;
            _held.ThrowIfTorn(this);
            _held.WriteOn(_stream, this);
        }

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
                _held?.WriteOn(_stream, this);
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
                this.BeforeWrite();
                // Held from here: a write that fails part-way may leave part of it in the stream.
                _held?.Wrote(this);
                try
                {
                    _stream.Position = _position;
                    _stream.Write(buffer, offset, count);
                }
                catch { _held?.Heard(); throw; }
                _position = _stream.Position;
            }
        }
    }
}
