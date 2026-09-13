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
            _streamPosition += count;

            _buffer.Write(buffer, offset, count);

            if (_buffer.Length >= MAX_CHUNK_SIZE)
            {
                this.WriteChunks(false);
            }
        }

        public override void Flush()
        {
            // write last unsaved chunks
            this.WriteChunks(true);
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
                        ["n"] = _file.Chunks++ // zero-based index
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