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
    /// Single instance of TempDisk manage read/write access to temporary disk - used in merge sort
    /// [ThreadSafe]
    /// </summary>
    internal class SortDisk : IDisposable
    {
        private readonly IStreamFactory _factory;
        private readonly StreamPool _pool;
        private readonly ConcurrentBag<long> _freePositions = new ConcurrentBag<long>();
        private long _lastContainerPosition = 0;
        private readonly int _containerSize;
        private readonly EnginePragmas _pragmas;

        public int ContainerSize => _containerSize;

        public SortDisk(IStreamFactory factory, int containerSize, EnginePragmas pragmas)
        {
            ENSURE(containerSize % PAGE_SIZE == 0, "size must be PAGE_SIZE multiple");

            _factory = factory;
            _containerSize = containerSize;
            _pragmas = pragmas;

            _lastContainerPosition = -containerSize;

            _pool = new StreamPool(_factory, false);
        }

        /// <summary>
        /// Get a new reader stream from pool. Must return after use
        /// </summary>
        public Stream GetReader()
        {
            return _pool.Rent();
        }

        /// <summary>
        /// Return used open reader stream to be reused in next sort
        /// </summary>
        public void Return(Stream stream)
        {
            _pool.Return(stream);
        }

        /// <summary>
        /// Return used disk container position to be reused in next sort
        /// </summary>
        public void Return(long position)
        {
            _freePositions.Add(position);
        }

        /// <summary>
        /// Get next avaiable disk position - can be a new extend file or reuse container slot
        /// Use thread safe classes to ensure multiple threads access at same time
        /// </summary>
        public long GetContainerPosition()
        {
            if (_freePositions.TryTake(out var position))
            {
                return position;
            }

            position = Interlocked.Add(ref _lastContainerPosition, _containerSize);

            return position;
        }

        /// <summary>
        /// Write buffer container data into disk
        /// </summary>
        public void Write(long position, BufferSlice buffer)
        {
            var writer = _pool.Writer.Value;

            // there is only a single writer instance, must be lock to ensure only 1 single thread are writing
            lock(writer)
            {
                for (var i = 0; i < _containerSize / PAGE_SIZE; ++i)
                {
                    writer.Position = position + i * PAGE_SIZE;
                    writer.Write(buffer.Array, buffer.Offset + i * PAGE_SIZE, PAGE_SIZE);
                }
            }
        }

        public void Dispose()
        {
            _pool.Dispose();

            _factory.Delete();
        }
    }
}
