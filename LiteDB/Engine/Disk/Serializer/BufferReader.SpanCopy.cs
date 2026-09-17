using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class BufferReader
    {
        /// <summary>
        /// Read bytes from the segmented source into a caller-owned span.
        /// </summary>
        public int Read(Span<byte> destination)
        {
            var written = 0;

            while (written < destination.Length)
            {
                var bytesLeft = _current.Count - _currentPosition;
                var bytesToCopy = Math.Min(destination.Length - written, bytesLeft);

                _current.EnsureReadable();
                new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, bytesToCopy)
                    .CopyTo(destination.Slice(written, bytesToCopy));

                written += bytesToCopy;

                this.MoveForward(bytesToCopy);

                if (_isEOF) break;
            }

            ENSURE(written == destination.Length, "current value must fit inside defined buffer");

            return written;
        }

        /// <summary>
        /// Expose the next bytes without copying when they fit in the current segment.
        /// The span is valid only until this reader advances or is disposed.
        /// </summary>
        public bool TryGetContiguousSpan(int count, out ReadOnlySpan<byte> span)
        {
            if (count >= 0 && _currentPosition + count <= _current.Count)
            {
                _current.EnsureReadable();
                span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, count);
                return true;
            }

            span = default;
            return false;
        }
    }
}
