using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class BufferWriter
    {
        private void WriteFrom(ReadOnlySpan<byte> source)
        {
            var written = 0;

            while (written < source.Length)
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
                var bytesToCopy = Math.Min(source.Length - written, bytesLeft);

                _current.EnsureWritable();
                source.Slice(written, bytesToCopy)
                    .CopyTo(new Span<byte>(_current.Array, _current.Offset + _currentPosition, bytesToCopy));

                written += bytesToCopy;

                this.MoveForward(bytesToCopy);
            }

            ENSURE(written == source.Length, "current value must fit inside defined buffer");
        }
    }
}
