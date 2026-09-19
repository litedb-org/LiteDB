using System;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream
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

        internal LiteFileStream(ILiteCollection<LiteFileInfo<TFileId>> files, ILiteCollection<BsonDocument> chunks, LiteFileInfo<TFileId> file, BsonValue fileId, FileAccess mode, bool preserveExisting = false)
        {
            _files = files;
            _chunks = chunks;
            _file = file;
            _fileId = fileId;
            _mode = mode;

            if (mode == FileAccess.Read)
            {
                if (_file.Length < 0 || _file.Chunks < 0 || (_file.Length == 0) != (_file.Chunks == 0))
                    throw new LiteException(LiteException.INVALID_FORMAT, "File '{0}' has inconsistent length and chunk metadata.", _fileId);
                // initialize first data block
                _currentChunkData = this.GetChunkData(_currentChunkIndex);
            }
            else if(mode == FileAccess.Write)
            {
                _buffer = new MemoryStream(MAX_CHUNK_SIZE);
                _staged = preserveExisting && _chunks.Exists(CHUNK_RANGE, _fileId, 0, int.MaxValue);

                // Replacement also repairs missing or orphaned chunks. Metadata may
                // describe an interrupted upload, so its count is not an oracle.
                if (!_staged) _chunks.DeleteMany(CHUNK_RANGE, _fileId, 0, int.MaxValue);
                _file.Length = 0;
                _file.Chunks = 0;
            }
        }

        /// <summary>
        /// Get file information
        /// </summary>
        public LiteFileInfo<TFileId> FileInfo { get { return _file; } }

        internal bool IsOwnedBy(ILiteCollection<LiteFileInfo<TFileId>> files, ILiteCollection<BsonDocument> chunks)
        {
            return ReferenceEquals(_files, files) && ReferenceEquals(_chunks, chunks);
        }

        public override long Length { get { return _file.Length; } }

        public override bool CanRead { get { return _mode == FileAccess.Read; } }

        public override bool CanWrite { get { return _mode == FileAccess.Write; } }

        public override bool CanSeek { get { return _mode == FileAccess.Read; } }

        public override long Position
        {
            get { return _streamPosition; }
            set { if (_mode == FileAccess.Read) { this.SetReadStreamPosition(value); } else { throw new NotSupportedException(); } }
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

        internal void Abort()
        {
            _disposed = true;
            _buffer?.Dispose();
        }

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

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        #endregion
    }
}
