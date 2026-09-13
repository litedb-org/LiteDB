using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Memory file reader - must call Dipose after use to return reader into pool
    /// This class is not ThreadSafe - must have 1 instance per thread (get instance from DiskService)
    /// </summary>
    internal class DiskReader : IDisposable
    {
        private readonly EngineState _state;
        private readonly MemoryCache _cache;

        private readonly StreamPool _dataPool;
        private readonly StreamPool _logPool;

        private readonly Lazy<Stream> _dataStream;
        private readonly Lazy<Stream> _logStream;

        public DiskReader(EngineState state, MemoryCache cache, StreamPool dataPool, StreamPool logPool)
        {
            _state = state;
            _cache = cache;
            _dataPool = dataPool;
            _logPool = logPool;

            _dataStream = new Lazy<Stream>(() => _dataPool.Rent());
            _logStream = new Lazy<Stream>(() => _logPool.Rent());
        }

        public PageBuffer ReadPage(long position, bool writable, FileOrigin origin)
        {
            ENSURE(position % PAGE_SIZE == 0, "invalid page position");

            var stream = origin == FileOrigin.Data ?
                _dataStream.Value :
                _logStream.Value;

            var page = writable ?
                _cache.GetWritablePage(position, origin, (pos, buf) => this.ReadStream(stream, pos, buf)) :
                _cache.GetReadablePage(position, origin, (pos, buf) => this.ReadStream(stream, pos, buf));

#if DEBUG
            _state.SimulateDiskReadFail?.Invoke(page);
#endif

            return page;
        }

        /// <summary>
        /// Read bytes from stream into buffer slice
        /// </summary>
        private void ReadStream(Stream stream, long position, BufferSlice buffer)
        {
            // can't test "Length" from out-to-date stream
            // ENSURE(stream.Length <= position - PAGE_SIZE, "can't be read from beyond file length");

            stream.Position = position;

            stream.Read(buffer.Array, buffer.Offset, buffer.Count);

            DEBUG(buffer.All(0) == false, "check if are not reading out of file length");
        }

        /// <summary>
        /// Request for a empty, writable non-linked page (same as DiskService.NewPage)
        /// </summary>
        public PageBuffer NewPage()
        {
            return _cache.NewPage();
        }

        /// <summary>
        /// When dispose, return stream to pool
        /// </summary>
        public void Dispose()
        {
            if (_dataStream.IsValueCreated)
            {
                _dataPool.Return(_dataStream.Value);
            }

            if (_logStream.IsValueCreated)
            {
                _logPool.Return(_logStream.Value);
            }
        }
    }
}