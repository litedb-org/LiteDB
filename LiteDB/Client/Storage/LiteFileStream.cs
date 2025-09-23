using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream, IAsyncDisposable
    {
        /// <summary>
        /// Number of bytes on each chunk document to store
        /// </summary>
        public const int MAX_CHUNK_SIZE = 255 * 1024; // 255kb like GridFS

        private readonly ILiteCollection<LiteFileInfo<TFileId>> _files;
        private readonly ILiteCollection<BsonDocument> _chunks;
        private readonly LiteFileInfo<TFileId> _file;
        private readonly BsonValue _fileId;
        private readonly FileAccess _mode;

        private long _streamPosition = 0;
        private int _currentChunkIndex = 0;
        private byte[] _currentChunkData = null;
        private int _positionInChunk = 0;
        private MemoryStream _buffer;

        internal LiteFileStream(ILiteCollection<LiteFileInfo<TFileId>> files, ILiteCollection<BsonDocument> chunks, LiteFileInfo<TFileId> file, BsonValue fileId, FileAccess mode)
        {
            _files = files;
            _chunks = chunks;
            _file = file;
            _fileId = fileId;
            _mode = mode;

            if (mode == FileAccess.Write)
            {
                _buffer = new MemoryStream(MAX_CHUNK_SIZE);
            }
        }

        /// <summary>
        /// Get file information
        /// </summary>
        public LiteFileInfo<TFileId> FileInfo => _file;

        public override long Length => _file.Length;

        public override bool CanRead => _mode == FileAccess.Read;

        public override bool CanWrite => _mode == FileAccess.Write;

        public override bool CanSeek => _mode == FileAccess.Read;

        public override long Position
        {
            get => _streamPosition;
            set
            {
                if (_mode == FileAccess.Read)
                {
                    this.SetReadStreamPosition(value);
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_mode == FileAccess.Write)
            {
                throw new NotSupportedException();
            }

            switch (origin)
            {
                case SeekOrigin.Begin:
                    this.SetReadStreamPosition(offset);
                    break;
                case SeekOrigin.Current:
                    this.SetReadStreamPosition(_streamPosition + offset);
                    break;
                case SeekOrigin.End:
                    this.SetReadStreamPosition(Length + offset);
                    break;
            }

            return _streamPosition;
        }

        #region Dispose

        private bool _disposed = false;

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            if (disposing && this.CanWrite)
            {
                this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
#if NETSTANDARD2_0
                _buffer?.Dispose();
#else
                if (_buffer != null)
                {
                    _buffer.Dispose();
                }
#endif
            }

            _disposed = true;

            base.Dispose(disposing);
        }

#if !NETSTANDARD2_0
        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (this.CanWrite)
            {
                await this.FlushAsync(CancellationToken.None).ConfigureAwait(false);
#if NETSTANDARD2_0
                _buffer?.Dispose();
#else
                if (_buffer != null)
                {
                    await _buffer.DisposeAsync().ConfigureAwait(false);
                }
#endif
            }

            _disposed = true;

            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

#if NETSTANDARD2_0
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            if (this.CanWrite)
            {
                await this.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                _buffer?.Dispose();
            }

            _disposed = true;

            base.Dispose(false);
        }
#endif

        #endregion

        #region Not supported operations

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        #endregion
    }
}
