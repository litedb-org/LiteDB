using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class BufferReader
    {
        private void Read(Span<byte> destination)
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
        }
    }
}
