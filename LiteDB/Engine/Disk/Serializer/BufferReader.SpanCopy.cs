using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class BufferReader
    {
        private void ReadInto(Span<byte> destination)
        {
            var written = 0;

            while (written < destination.Length)
            {
                if (_isEOF && _currentPosition == _current.Count)
                {
                    break;
                }

                var bytesLeft = _current.Count - _currentPosition;
                if (bytesLeft == 0)
                {
                    this.MoveForward(0);
                    continue;
                }
                var bytesToCopy = Math.Min(destination.Length - written, bytesLeft);

                _current.EnsureReadable();
                new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, bytesToCopy)
                    .CopyTo(destination.Slice(written, bytesToCopy));

                written += bytesToCopy;

                this.MoveForward(bytesToCopy);
            }

            ENSURE(written == destination.Length, "current value must fit inside defined buffer");
        }
    }
}
