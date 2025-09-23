using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            this.WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_mode != FileAccess.Write) throw new NotSupportedException();

            _streamPosition += count;

            await _buffer.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);

            if (_buffer.Length >= MAX_CHUNK_SIZE)
            {
                await this.WriteChunksAsync(flush: false, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Flush()
        {
            this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return this.WriteChunksAsync(flush: true, cancellationToken);
        }

        private async Task WriteChunksAsync(bool flush, CancellationToken cancellationToken)
        {
            if (_buffer == null || _buffer.Length == 0)
            {
                if (flush && _buffer != null)
                {
                    _buffer.SetLength(0);
                    _buffer.Position = 0;
                }

                if (flush)
                {
                    _file.UploadDate = DateTime.Now;
                    _file.Length = _streamPosition;

                    await _files.UpsertAsync(_file, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var chunkBuffer = new byte[MAX_CHUNK_SIZE];

            _buffer.Seek(0, SeekOrigin.Begin);

            int read;
            while ((read = await _buffer.ReadAsync(chunkBuffer, 0, MAX_CHUNK_SIZE, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var chunk = new BsonDocument
                {
                    ["_id"] = new BsonDocument
                    {
                        ["f"] = _fileId,
                        ["n"] = _file.Chunks++
                    }
                };

                var bytes = new byte[read];
                Buffer.BlockCopy(chunkBuffer, 0, bytes, 0, read);

                chunk["data"] = bytes;

                await _chunks.InsertAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            if (flush)
            {
                _file.UploadDate = DateTime.Now;
                _file.Length = _streamPosition;

                await _files.UpsertAsync(_file, cancellationToken).ConfigureAwait(false);
            }

            _buffer.SetLength(0);
            _buffer.Position = 0;
        }
    }
}
