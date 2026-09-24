using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Manage multiple open readonly Stream instances from same source (file). 
    /// Support single writer instance
    /// Close all Stream on dispose
    /// [ThreadSafe]
    /// </summary>
    internal class StreamPool : IDisposable
    {
        private readonly ConcurrentBag<Stream> _pool = new ConcurrentBag<Stream>();
        private readonly Lazy<Stream> _writer;
        private readonly IStreamFactory _factory;
        private Stream _createdWriter;
        private int _disposed;

        public StreamPool(IStreamFactory factory, bool appendOnly)
        {
            _factory = factory;

            _writer = new Lazy<Stream>(() => this.CreateWriter(appendOnly), true);
        }

        /// <summary>
        /// Get single Stream writer instance
        /// </summary>
        public Lazy<Stream> Writer => _writer;

        /// <summary>
        /// Rent a Stream reader instance
        /// </summary>
        public Stream Rent()
        {
            this.ThrowIfDisposed();
            if (!_pool.TryTake(out var stream))
            {
                stream = _factory.GetStream(false, false);
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_factory.CloseOnDispose) stream.Dispose();
                throw new ObjectDisposedException(nameof(StreamPool));
            }
            return stream;
        }

        /// <summary>
        /// After use, return Stream reader instance
        /// </summary>
        public void Return(Stream stream)
        {
            _pool.Add(stream);
            // Either Dispose drains this return, or we observe it and drain.
            // The bag transfers ownership to exactly one of those callers.
            if (Volatile.Read(ref _disposed) != 0)
            {
                var errors = new List<Exception>();
                this.DrainReaders(errors);
                if (errors.Count > 0) throw new AggregateException(errors);
            }
        }

        private Stream CreateWriter(bool appendOnly)
        {
            this.ThrowIfDisposed();
            var stream = _factory.GetStream(true, appendOnly);
            Interlocked.Exchange(ref _createdWriter, stream);
            // Lazy.IsValueCreated is still false inside this callback. Publish
            // ownership separately so concurrent disposal cannot miss the writer.
            if (Volatile.Read(ref _disposed) != 0)
            {
                var abandoned = Interlocked.Exchange(ref _createdWriter, null);
                if (_factory.CloseOnDispose) abandoned?.Dispose();
                throw new ObjectDisposedException(nameof(StreamPool));
            }
            return stream;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(StreamPool));
        }

        private void DrainReaders(ICollection<Exception> errors)
        {
            while (_pool.TryTake(out var stream))
            {
                if (_factory.CloseOnDispose) TryDispose(stream, errors);
            }
        }

        /// <summary>
        /// Close all Stream instances (readers/writer)
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var errors = new List<Exception>();

            this.DrainReaders(errors);
            var writer = Interlocked.Exchange(ref _createdWriter, null);
            if (_factory.CloseOnDispose && writer != null) TryDispose(writer, errors);

            TryDispose(_factory, errors);

            if (errors.Count > 0) throw new AggregateException(errors);
        }

        private static void TryDispose(IDisposable disposable, ICollection<Exception> errors)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
    }
}
