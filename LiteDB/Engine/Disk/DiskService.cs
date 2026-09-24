using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static LiteDB.Constants;
namespace LiteDB.Engine
{
    /// <summary>
    /// Implement custom fast/in memory mapped disk access
    /// [ThreadSafe]
    /// </summary>
    internal partial class DiskService : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly EngineState _state;
        private readonly bool _readOnly;
        private readonly ICoordinationSignals _signals;
        internal bool CompactStorage { get; }

        private IStreamFactory _dataFactory;
        private readonly IStreamFactory _logFactory;

        private StreamPool _dataPool;
        private readonly StreamPool _logPool;
        private readonly Lazy<Stream> _writer;

        private long _dataLength;
        private long _logLength;
        private long _dataTrailingLength;
        private long _logTrailingLength;
        private int _disposed;

        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        public DiskService(
            EngineSettings settings,
            EngineState state,
            int[] memorySegmentSizes)
        {
            if (!Enum.IsDefined(typeof(CompactStorageMode), settings.CompactStorage))
            {
                throw new ArgumentOutOfRangeException(nameof(settings.CompactStorage));
            }

            _cache = new MemoryCache(memorySegmentSizes, settings.GetCacheSize());
            _state = state;
            _readOnly = settings.ReadOnly;
            _durableCommits = settings.DurableCommits;
            _sharedDurability = settings.SharedDurability;
            _signals = settings.CoordinationSignals;

            try
            {
                _dataFactory = settings.CreateDataFactory();
                _logFactory = new ChecksummedWalFactory(settings.CreateLogFactory(), _checksums);

                _dataPool = new StreamPool(_dataFactory, false);
                _logPool = new StreamPool(_logFactory, true);
                _writer = _logPool.Writer;

                var dataLength = _dataFactory.GetLength();
                var isNew = dataLength == 0L;

                if (isNew)
                {
                    if (settings.ReadOnly)
                    {
                        // Open missing sources only to preserve the underlying path error.
                        // Never initialize an empty file or caller-owned stream in read-only mode.
                        if (_dataFactory is FileStreamFactory && !_dataFactory.Exists())
                        {
                            using (var source = _dataFactory.GetStream(false, false)) { }
                        }
                        throw new LiteException(LiteException.INVALID_DATABASE,
                            "Database '{0}' is empty and cannot be initialized in read-only mode.",
                            settings.Filename ?? _dataFactory.Name);
                    }
                    LOG($"creating new database: '{Path.GetFileName(_dataFactory.Name)}'", "DISK");

                    this.Initialize(_dataPool.Writer.Value, settings.Collation, settings.InitialSize,
                        settings.CompactStorage == CompactStorageMode.Auto);
                    dataLength = _dataFactory.GetLength();
                }

                if (dataLength < PAGE_SIZE) throw LiteException.InvalidDatabase();
                if (!isNew) this.ValidateExistingData();
                CompactStorage = settings.CompactStorage != CompactStorageMode.Legacy;

                if (settings.ReadOnly == false)
                {
                    _ = _dataPool.Writer.Value.CanRead;
                }

                _dataTrailingLength = dataLength % PAGE_SIZE;
                _dataLength = dataLength - _dataTrailingLength - PAGE_SIZE;

                if (_logFactory.Exists())
                {
                    var logLength = _logFactory.GetLength();
                    _logTrailingLength = ((ChecksummedWalFactory)_logFactory).TrailingBytes;
                    _logLength = logLength - logLength % PAGE_SIZE - PAGE_SIZE;
                }
                else
                {
                    _logLength = -PAGE_SIZE;
                }
            }
            catch
            {
                TryDispose(_dataPool);
                TryDispose(_logPool);
                TryDispose(_dataFactory);
                TryDispose(_logFactory);
                TryDispose(_cache);
                throw;
            }
        }

        /// <summary>
        /// Get memory cache instance
        /// </summary>
        public MemoryCache Cache => _cache;

        /// <summary>
        /// Get a new instance for read data/log pages. This instance are not thread-safe - must request 1 per thread (used in Transaction)
        /// </summary>
        public DiskReader GetReader()
        {
            return new DiskReader(_state, _cache, _dataPool, _logPool, ChecksumsEnabled ? _dataChecksums : null);
        }

        /// <summary>
        /// This method calculates the maximum number of items (documents or IndexNodes) that this database can have.
        /// The result is used to prevent infinite loops in case of problems with pointers
        /// Each page support max of 255 items. Use 10 pages offset (avoid empty disk)
        /// </summary>
        public uint MAX_ITEMS_COUNT => (uint)(((_dataLength + _logLength) / PAGE_SIZE) + 10) * byte.MaxValue;

        /// <summary>
        /// When a page are requested as Writable but not saved in disk, must be discard before release
        /// </summary>
        public void DiscardDirtyPages(IEnumerable<PageBuffer> pages)
        {
            // only for ROLLBACK action
            foreach (var page in pages)
            {
                // complete discard page and content
                _cache.DiscardPage(page);
            }
        }

        /// <summary>
        /// Discard pages that contains valid data and was not modified
        /// </summary>
        public void DiscardCleanPages(IEnumerable<PageBuffer> pages)
        {
            foreach (var page in pages)
            {
                // if page was not modified, try move to readable list
                if (_cache.TryMoveToReadable(page) == false)
                {
                    // if already in readable list, just discard
                    _cache.DiscardPage(page);
                }
            }
        }

        /// <summary>
        /// Request for a empty, writable non-linked page.
        /// </summary>
        public PageBuffer NewPage()
        {
            return _cache.NewPage();
        }

        /// <summary>
        /// Get file length based on data/log length variables (no direct on disk)
        /// </summary>
        public long GetFileLength(FileOrigin origin)
        {
            if (origin == FileOrigin.Log)
            {
                return _logLength + PAGE_SIZE;
            }
            else
            {
                return _dataLength + PAGE_SIZE;
            }
        }

        /// <summary>
        /// Mark the header for recovery during error-close, before disposing
        /// the data writer and its factory (which may own a shared stream).
        /// </summary>
        internal void MarkAsInvalidState()
        {
            // Never ended: after error-close no client may open a direct snapshot.
            _signals?.StructuralBegin();
            FileHelper.TryExec(60, () =>
            {
                var stream = _dataPool.Writer.Value;
                var buffer = _bufferPool.Rent(PAGE_SIZE);
                try
                {
                    stream.Position = 0;
                    var offset = 0;
                    while (offset < PAGE_SIZE)
                    {
                        var read = stream.Read(buffer, offset, PAGE_SIZE - offset);
                        if (read == 0) throw new EndOfStreamException("Cannot mark an incomplete database header");
                        offset += read;
                    }
                    this.MarkHeaderInvalid(new BufferSlice(buffer, 0, PAGE_SIZE));
                    stream.Position = 0;
                    stream.Write(buffer, 0, PAGE_SIZE);
                    stream.FlushToDisk();
                }
                finally
                {
                    _bufferPool.Return(buffer, true);
                }
            });
        }

        #region Sync Read/Write operations

        /// <summary>
        /// Experimental coordinator: the WAL frames from <paramref name="from"/> to the
        /// current physical end, validated as <see cref="ReadFull"/> validates them.
        /// </summary>
        internal IEnumerable<PageBuffer> ReadLogFrom(long from)
        {
            if (!ChecksumsEnabled || !_logFactory.Exists()) yield break;
            var length = _logFactory.GetLength();
            if (length <= from) yield break;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                foreach (var page in WalRetirementReader.Read(reader.RawStream, _checksums, length, null, from))
                    yield return page;
            }
            finally { _logPool.Return(reader); }
        }

        /// <summary>
        /// Read all database pages inside file with no cache using. PageBuffers dont need to be Released
        /// </summary>
        public IEnumerable<PageBuffer> ReadFull(FileOrigin origin)
        {
            if (this.GetFileLength(origin) == 0) yield break;
            if (origin == FileOrigin.Log && ChecksumsEnabled)
            {
                var reader = (ChecksummedWalStream)_logPool.Rent();
                try
                {
                    foreach (var page in WalRetirementReader.Read(reader.RawStream, _checksums, GetFileLength(origin)))
                        yield return page;
                }
                finally { _logPool.Return(reader); }
                yield break;
            }
            // do not use MemoryCache factory - reuse same buffer array (one page per time)
            // do not use BufferPool because header page can't be shared (byte[] is used inside page return)
            var buffer = new byte[PAGE_SIZE];

            var pool = origin == FileOrigin.Log ? _logPool : _dataPool;
            var stream = pool.Rent();

            try
            {
                // get length before starts (avoid grow during loop)
                var length = this.GetFileLength(origin);

                stream.Position = 0;

                while (stream.Position < length)
                {
                    var position = stream.Position;

                    var bytesRead = stream.ReadFully(buffer, 0, PAGE_SIZE);

                    if (bytesRead != PAGE_SIZE && origin == FileOrigin.Log && ChecksumsEnabled)
                        throw new PageChecksumException(origin, position);
                    ENSURE(bytesRead == PAGE_SIZE, "ReadFull must read PAGE_SIZE bytes [{0}]", bytesRead);
                    this.ReadRecoveredHeader(buffer, position, origin);
                    if (origin == FileOrigin.Data && ChecksumsEnabled)
                        _dataChecksums.Validate(new BufferSlice(buffer, 0, PAGE_SIZE), position);

                    yield return new PageBuffer(buffer, 0, 0)
                    {
                        Position = position,
                        Origin = origin,
                        WalFrame = origin == FileOrigin.Log ? ((ChecksummedWalStream)stream).LastFrame : default,
                        ShareCounter = 0
                    };
                }
            }
            finally
            {
                pool.Return(stream);
            }
        }

        /// <summary>
        /// Write pages DIRECT in disk. This pages are not cached and are not shared - WORKS FOR DATA FILE ONLY
        /// </summary>
        public void WriteDataDisk(IEnumerable<PageBuffer> pages)
        {
            var stream = _dataPool.Writer.Value;
            lock (stream)
            {
                foreach (var page in pages)
                {
                    ENSURE(page.ShareCounter == 0, "this page can't be shared to use sync operation - do not use cached pages");

                    _dataLength = Math.Max(_dataLength, page.Position);

                    stream.Position = page.Position;

                    this.CrashPoint("checkpoint-before-page-write");
                    this.PreserveFileVersion(page);
                    this.StampDataPage(page);
                    stream.Write(page.Array, page.Offset, PAGE_SIZE);
                    this.CrashPoint("checkpoint-after-page-write");
                    this.CheckpointStage("data-page");
                }

                this.CrashPoint("checkpoint-before-data-flush");
                stream.FlushToDisk();
                this.CrashPoint("checkpoint-after-data-flush");
                this.CheckpointStage("data-flushed");
            }
        }

        /// <summary>
        /// Set new length for file in sync mode. Queue must be empty before set length
        /// </summary>
        public void SetLength(long length, FileOrigin origin)
        {
            var stream = origin == FileOrigin.Log ? _logPool.Writer : _dataPool.Writer;

            if (origin == FileOrigin.Log)
            {
                Interlocked.Exchange(ref _logLength, length - PAGE_SIZE);
                if (length == 0)
                {
                    _freeLogPositions.Clear();
                    _lastLogPositions.Clear();
                    _lastWalTransactionID = 0;
                }
            }
            else
            {
                Interlocked.Exchange(ref _dataLength, length - PAGE_SIZE);
            }

            stream.Value.SetLength(length);

            if (origin == FileOrigin.Log)
            {
                _logFactory.TrimCapacity(stream.Value);
            }
        }

        /// <summary>
        /// Get file name (or Stream name)
        /// </summary>
        public string GetName(FileOrigin origin)
        {
            return origin == FileOrigin.Data ? _dataFactory.Name : _logFactory.Name;
        }

        #endregion

    }
}
