using System;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_mode != FileAccess.Write) throw new NotSupportedException();

            try
            {
                _buffer.Write(buffer, offset, count);
            }
            catch
            {
                if (_complete != null) _failed = true;
                throw;
            }

            _streamPosition += count;

            if (_buffer.Length >= MAX_CHUNK_SIZE)
            {
                try
                {
                    this.WriteChunks(false);
                }
                catch
                {
                    _failed = true;
                    throw;
                }
            }
        }

        public override void Flush()
        {
            if (_mode != FileAccess.Write) return;

            try
            {
                // write last unsaved chunks
                this.WriteChunks(true);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        private void InitializeAppend()
        {
            _buffer = new MemoryStream(MAX_CHUNK_SIZE);
            _streamPosition = _file.Length;

            if (_file.Chunks == 0) return;

            var chunkIndex = _file.Chunks - 1;
            var chunkId = new BsonDocument
            {
                ["f"] = _fileId,
                ["n"] = chunkIndex
            };
            var chunk = _chunks.Query()
                .Where("_id = { f: @0, n: @1 }", _fileId, chunkIndex)
                .ForUpdate()
                .FirstOrDefault();

            ENSURE(chunk != null);

            var data = chunk["data"].AsBinary;

            if (data.Length < MAX_CHUNK_SIZE)
            {
                _buffer.Write(data, 0, data.Length);
                ENSURE(_chunks.Delete(chunkId));
                _file.Chunks--;
            }
        }

        /// <summary>
        /// Consume all _buffer bytes and write to chunk collection
        /// </summary>
        private void WriteChunks(bool flush)
        {
            var buffer = new byte[MAX_CHUNK_SIZE];
            var read = 0;

            _buffer.Seek(0, SeekOrigin.Begin);

            while ((read = _buffer.Read(buffer, 0, MAX_CHUNK_SIZE)) > 0)
            {
                var chunk = new BsonDocument
                {
                    ["_id"] = new BsonDocument
                    {
                        ["f"] = _fileId,
                        ["n"] = _file.Chunks // zero-based index
                    }
                };

                // get chunk byte array part
                if (read != MAX_CHUNK_SIZE)
                {
                    var bytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, bytes, 0, read);
                    chunk["data"] = bytes;
                }
                else
                {
                    chunk["data"] = buffer;
                }

                // insert chunk part
                _chunks.Insert(chunk);
                _file.Chunks++;
            }

            // if stream was closed/flush, update file too
            if (flush)
            {
                _file.UploadDate = DateTime.Now;
                _file.Length = _streamPosition;

                _files.Upsert(_file);
            }

            _buffer = new MemoryStream();
        }
    }
}
