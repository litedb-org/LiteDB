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

            try
            {
                _dataFactory = settings.CreateDataFactory();
                _logFactory = settings.CreateLogFactory();

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
                if (!isNew)
                {
                    this.RecoverHeaderPromotion();
                    this.ValidateExistingData();
                }
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
                    _logTrailingLength = logLength % PAGE_SIZE;
                    _logLength = logLength - _logTrailingLength - PAGE_SIZE;
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
            return new DiskReader(_state, _cache, _dataPool, _logPool);
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
        /// Write all pages inside log file in a thread safe operation.
        /// Takes ownership of each yielded frame, including on failure.
        /// </summary>
        public int WriteLogDisk(IEnumerable<PageBuffer> pages, Action<uint, long> written = null,
            IReadOnlyDictionary<uint, PagePosition> transactionPages = null)
        {
            var count = 0;
            var hasConfirmation = false;
            var stream = _writer.Value;

            // do a global write lock - only 1 thread can write on disk at time
            lock(stream)
            {
                foreach (var page in pages)
                {
                    var previousLogLength = _logLength;
                    long? previousStreamLength = null;
                    PageBuffer readable = null;

                    try
                    {
                        ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");
                        previousStreamLength = stream.Length;
                        var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
                        // Only this transaction can see its unconfirmed slots. Keep
                        // the confirmation page last so recovery sees every page.
                        if (!page.ReadBool(BasePage.P_IS_CONFIRMED) && transactionPages != null &&
                            transactionPages.TryGetValue(pageID, out var previous))
                        {
                            page.Position = previous.Position;
                            _cache.Invalidate(page.Position, FileOrigin.Log);
                        }
                        else
                        {
                            page.Position = Interlocked.Add(ref _logLength, PAGE_SIZE);
                        }
                        page.Origin = FileOrigin.Log;
                        stream.Position = page.Position;

#if DEBUG || TESTING
                        _state.SimulateDiskWriteFail?.Invoke(page);
#endif

                        this.CrashPoint(page.ReadBool(BasePage.P_IS_CONFIRMED) ?
                            "wal-confirmation-before-write" : "wal-page-before-write");
                        this.PreserveFileVersion(page);
                        stream.Write(page.Array, page.Offset, PAGE_SIZE);
                        this.CrashPoint(page.ReadBool(BasePage.P_IS_CONFIRMED) ?
                            "wal-confirmation-after-write" : "wal-page-after-write");
                        hasConfirmation |= page.ReadBool(BasePage.P_IS_CONFIRMED);

                        // Publish only after the bytes are written to the stream.
                        // The callback can make the position visible to readers.
                        readable = _cache.MoveToReadable(page);

                        written?.Invoke(pageID, readable.Position);

                        count++;
                    }
                    catch
                    {
                        // The producer transferred ownership before yielding.
                        // Recycle failed frames and undo unpublished reservations.
                        if (readable == null && page.State == FrameState.Writable)
                        {
                            _cache.DiscardPage(page);
                            Interlocked.Exchange(ref _logLength, previousLogLength);
                            if (previousStreamLength.HasValue)
                            {
                                stream.SetLength(previousStreamLength.Value);
                                _logFactory.TrimCapacity(stream);
                            }
                        }

                        throw;
                    }
                    finally
                    {
                        readable?.Release();
                    }
                }
                // A confirmation makes this WAL batch recoverable. Make all preceding
                // pages durable before WAL-index confirmation or acknowledging commit.
                if (hasConfirmation)
                {
                    try
                    {
                        this.CrashPoint("wal-before-durable-flush");
                        this.FlushConfirmedLog(stream);
                        this.CrashPoint("wal-after-durable-flush");
                    }
                    catch (Exception ex)
                    {
                        // The confirmation may already be durable. Stop the engine;
                        // rollback or further writes cannot resolve this uncertainty.
                        var failure = ex as IOException ?? new IOException("WAL durable flush failed.", ex);
                        _state.Handle(failure);
                        if (failure == ex) throw;
                        throw failure;
                    }
                }
                else stream.Flush();
            }

            return count;
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
                    buffer[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
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
        /// Read all database pages inside file with no cache using. PageBuffers dont need to be Released
        /// </summary>
        public IEnumerable<PageBuffer> ReadFull(FileOrigin origin)
        {
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

                    ENSURE(bytesRead == PAGE_SIZE, "ReadFull must read PAGE_SIZE bytes [{0}]", bytesRead);

                    if (origin == FileOrigin.Data && position == 0 && _readOnlyPromotionHeader != null)
                        Buffer.BlockCopy(_readOnlyPromotionHeader, 0, buffer, 0, PAGE_SIZE);

                    yield return new PageBuffer(buffer, 0, 0)
                    {
                        Position = position,
                        Origin = origin,
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

            foreach (var page in pages)
            {
                ENSURE(page.ShareCounter == 0, "this page can't be shared to use sync operation - do not use cached pages");

                _dataLength = Math.Max(_dataLength, page.Position);

                stream.Position = page.Position;

                this.CrashPoint("checkpoint-before-page-write");
                this.PreserveFileVersion(page);
                stream.Write(page.Array, page.Offset, PAGE_SIZE);
                this.CrashPoint("checkpoint-after-page-write");
            }

            this.CrashPoint("checkpoint-before-data-flush");
            stream.FlushToDisk();
            this.CrashPoint("checkpoint-after-data-flush");
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
            }
            else
            {
                Interlocked.Exchange(ref _dataLength, length - PAGE_SIZE);
            }

            stream.Value.SetLength(length);

            if (origin == FileOrigin.Log)
            {
                if (length == 0) this.RetireHeaderPromotion(stream.Value);
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var errors = new List<Exception>();
            var delete = false;

            TryAction(() => delete = _logFactory.Exists() && _logPool.Writer.Value.Length == 0, errors);
            TryAction(() => _dataPool.Dispose(), errors);
            TryAction(() => _logPool.Dispose(), errors);
            if (delete) TryAction(() => _logFactory.Delete(), errors);
            TryAction(() => _cache.Dispose(), errors);

            if (errors.Count > 0) throw new AggregateException(errors);
        }

        private static void TryDispose(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Constructor cleanup must preserve the initialization error
                // while still attempting every remaining resource.
            }
        }

        private static void TryAction(Action action, ICollection<Exception> errors)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
    }
}
