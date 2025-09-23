using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public partial class LiteFileStream<TFileId> : Stream
    {
        private readonly Dictionary<int, long> _chunkLengths = new Dictionary<int, long>();

        public override int Read(byte[] buffer, int offset, int count)
        {
            return this.ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_mode != FileAccess.Read) throw new NotSupportedException();
            if (_streamPosition == Length)
            {
                return 0;
            }

            if (_currentChunkData == null)
            {
                _currentChunkData = await this.GetChunkDataAsync(_currentChunkIndex, cancellationToken).ConfigureAwait(false);
            }

            var bytesLeft = count;

            while (_currentChunkData != null && bytesLeft > 0)
            {
                var bytesToCopy = Math.Min(bytesLeft, _currentChunkData.Length - _positionInChunk);

                Buffer.BlockCopy(_currentChunkData, _positionInChunk, buffer, offset, bytesToCopy);

                _positionInChunk += bytesToCopy;
                bytesLeft -= bytesToCopy;
                offset += bytesToCopy;
                _streamPosition += bytesToCopy;

                if (_positionInChunk >= _currentChunkData.Length)
                {
                    _positionInChunk = 0;
                    _currentChunkData = await this.GetChunkDataAsync(++_currentChunkIndex, cancellationToken).ConfigureAwait(false);
                }
            }

            return count - bytesLeft;
        }

        private void SetReadStreamPosition(long newPosition)
        {
            if (newPosition < 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (newPosition >= Length)
            {
                _streamPosition = Length;
                return;
            }

            _streamPosition = newPosition;

            long seekStreamPosition = 0;
            int loadedChunk = _currentChunkIndex;
            int newChunkIndex = 0;

            while (seekStreamPosition <= _streamPosition)
            {
                if (_chunkLengths.TryGetValue(newChunkIndex, out long length))
                {
                    seekStreamPosition += length;
                }
                else
                {
                    loadedChunk = newChunkIndex;
                    _currentChunkData = GetChunkDataSync(newChunkIndex);
                    if (_currentChunkData == null)
                    {
                        break;
                    }

                    seekStreamPosition += _currentChunkData.Length;
                }

                newChunkIndex++;
            }

            newChunkIndex--;

            if (newChunkIndex >= 0 && _chunkLengths.TryGetValue(newChunkIndex, out long chunkLength))
            {
                seekStreamPosition -= chunkLength;
            }

            _positionInChunk = (int)(_streamPosition - seekStreamPosition);
            _currentChunkIndex = Math.Max(0, newChunkIndex);

            if (loadedChunk != _currentChunkIndex)
            {
                _currentChunkData = GetChunkDataSync(_currentChunkIndex);
            }
        }

        private async Task<byte[]> GetChunkDataAsync(int index, CancellationToken cancellationToken)
        {
            var chunkId = new BsonDocument
            {
                ["f"] = _fileId,
                ["n"] = index
            };

            var chunk = await _chunks.FindByIdAsync(chunkId, cancellationToken).ConfigureAwait(false);

            byte[] result = chunk?["data"].AsBinary;
            if (result != null)
            {
                _chunkLengths[index] = result.Length;
            }

            return result;
        }

        private byte[] GetChunkDataSync(int index)
        {
            return this.GetChunkDataAsync(index, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
