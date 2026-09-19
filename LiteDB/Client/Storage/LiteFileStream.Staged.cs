using System.IO;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream
    {
        private const string CHUNK_RANGE = "_id BETWEEN { f: @0, n: @1 } AND { f: @0, n: @2 }";

        // A transaction has no savepoint, so a replacement that must be undone without rolling
        // back keeps the previous chunks and writes the new ones under negative indexes, which
        // no reader or delete range covers, until the whole content has arrived.
        private bool _staged;

        private int ChunkIndex(int number) => _staged ? int.MinValue + number : number;

        private BsonDocument ChunkId(int index) => new BsonDocument { ["f"] = _fileId, ["n"] = index };

        /// <summary>
        /// Remove every chunk this writer inserted and leave the previous file as it was.
        /// </summary>
        internal void Discard()
        {
            this.Abort();
            _chunks.DeleteMany(CHUNK_RANGE, _fileId, this.ChunkIndex(0), this.ChunkIndex(int.MaxValue));
        }

        private void PublishStagedChunks()
        {
            if (!_staged) return;

            _chunks.DeleteMany(CHUNK_RANGE, _fileId, 0, int.MaxValue);

            for (var number = 0; number < _file.Chunks; number++)
            {
                var stagedId = this.ChunkId(this.ChunkIndex(number));
                var chunk = _chunks.FindById(stagedId);

                _chunks.Delete(stagedId);
                chunk["_id"] = this.ChunkId(number);
                _chunks.Insert(chunk);
            }

            _staged = false;
        }
    }
}
