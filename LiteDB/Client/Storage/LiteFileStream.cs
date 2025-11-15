using System;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Provides a stream interface for reading from or writing to files stored in chunks within a LiteDB database.
    /// Supports sequential access to file data using a file identifier of type <typeparamref name="TFileId"/>.
    /// </summary>
    /// <typeparam name="TFileId">The type used to uniquely identify files within the LiteDB file storage system.</typeparam>
    public partial class LiteFileStream<TFileId> : Stream
    {
        /// <summary>
        /// Represents the maximum allowed chunk size, in bytes, for data segments. This value is set to 255 kilobytes.
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

            if (mode == FileAccess.Read)
            {
                // initialize first data block
                _currentChunkData = this.GetChunkData(_currentChunkIndex);
            }
            else if(mode == FileAccess.Write)
            {
                _buffer = new MemoryStream(MAX_CHUNK_SIZE);

                if (_file.Length > 0)
                {
                    // delete all chunks before re-write
                    var count = _chunks.DeleteMany("_id BETWEEN { f: @0, n: 0 } AND { f: @0, n: 99999999 }", _fileId);

                    ENSURE(count == _file.Chunks);

                    // clear file content length+chunks
                    _file.Length = 0;
                    _file.Chunks = 0;
                }
            }
        }

        /// <summary>
        /// Gets information about the associated file, including its identifier and metadata.
        /// </summary>
        public LiteFileInfo<TFileId> FileInfo { get { return _file; } }

        /// <inheritdoc/>
        public override long Length { get { return _file.Length; } }

        /// <inheritdoc/>
        public override bool CanRead { get { return _mode == FileAccess.Read; } }

        /// <inheritdoc/>
        public override bool CanWrite { get { return _mode == FileAccess.Write; } }

        /// <inheritdoc/>
        public override bool CanSeek { get { return _mode == FileAccess.Read; } }

        /// <inheritdoc/>
        public override long Position
        {
            get { return _streamPosition; }
            set { if (_mode == FileAccess.Read) { this.SetReadStreamPosition(value); } else { throw new NotSupportedException(); } }
        }

        /// <inheritdoc/>
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
            base.Dispose(disposing);

            if (_disposed) return;

            if (disposing && this.CanWrite)
            {
                this.Flush();
                _buffer?.Dispose();
            }

            _disposed = true;
        }

        #endregion

        #region Not supported operations

        /// <summary>
        /// Throws a <see cref="NotSupportedException"/> to indicate that setting the length of the stream is not
        /// supported.
        /// </summary>
        /// <remarks>This method is not supported and cannot be used to change the length of the stream.
        /// Attempting to call this method will always result in a <see cref="NotSupportedException"/> being
        /// thrown.</remarks>
        /// <param name="value">The desired length of the stream in bytes. This parameter is not used, as the operation is not supported.</param>
        /// <exception cref="NotSupportedException">Always thrown to indicate that setting the length of the stream is not supported.</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        #endregion
    }
}