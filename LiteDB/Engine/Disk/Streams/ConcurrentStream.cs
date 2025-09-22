using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        private long _position = 0;
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        public ConcurrentStream(Stream stream, bool canWrite)
        {
            _stream = stream;
            _canWrite = canWrite;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _canWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _position; set => _position = value; }

        public override void Flush()
        {
            _mutex.Wait();

            try
            {
                _stream.Flush();
            }
            finally
            {
                _mutex.Release();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return this.WithLockAsync(() => _stream.FlushAsync(cancellationToken), cancellationToken);
        }

        public override void SetLength(long value) => _stream.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            _stream.Dispose();
            _mutex.Dispose();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _mutex.Wait();

            try
            {
                var position =
                    origin == SeekOrigin.Begin ? offset :
                    origin == SeekOrigin.Current ? _position + offset :
                    _position - offset;

                _position = position;

                return _position;
            }
            finally
            {
                _mutex.Release();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _mutex.Wait();

            try
            {
                _stream.Position = _position;
                var read = _stream.Read(buffer, offset, count);
                _position = _stream.Position;
                return read;
            }
            finally
            {
                _mutex.Release();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_canWrite == false) throw new NotSupportedException("Current stream are readonly");

            _mutex.Wait();

            try
            {
                _stream.Position = _position;
                _stream.Write(buffer, offset, count);
                _position = _stream.Position;
            }
            finally
            {
                _mutex.Release();
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _stream.Position = _position;
                var read = await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                _position = _stream.Position;
                return read;
            }
            finally
            {
                _mutex.Release();
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_canWrite == false) throw new NotSupportedException("Current stream are readonly");

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _stream.Position = _position;
                await _stream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                _position = _stream.Position;
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task WithLockAsync(Func<Task> body, CancellationToken cancellationToken)
        {
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await body().ConfigureAwait(false);
            }
            finally
            {
                _mutex.Release();
            }
        }
    }
}