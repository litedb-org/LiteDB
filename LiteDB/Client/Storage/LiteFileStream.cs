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
        /// Get file information
        /// </summary>
        public LiteFileInfo<TFileId> FileInfo { get { return _file; } }

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