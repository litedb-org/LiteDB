using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

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
        private int _disposed;

        public StreamPool(IStreamFactory factory, bool appendOnly)
        {
            _factory = factory;

            _writer = new Lazy<Stream>(() => _factory.GetStream(true, appendOnly), true);
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
            if (!_pool.TryTake(out var stream))
            {
                stream = _factory.GetStream(false, false);
            }

            return stream;
        }

        /// <summary>
        /// After use, return Stream reader instance
        /// </summary>
        public void Return(Stream stream)
        {
            _pool.Add(stream);
        }

        /// <summary>
        /// Close all Stream instances (readers/writer)
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var errors = new List<Exception>();

            if (_factory.CloseOnDispose)
            {
                while (_pool.TryTake(out var stream))
                {
                    TryDispose(stream, errors);
                }

                if (_writer.IsValueCreated)
                {
                    TryDispose(_writer.Value, errors);
                }
            }

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
