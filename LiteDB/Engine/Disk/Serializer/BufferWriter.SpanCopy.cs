using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class BufferWriter
    {
        private void Write(ReadOnlySpan<byte> source)
        {
            var written = 0;

            while (written < source.Length)
            {
                var bytesLeft = _current.Count - _currentPosition;
                var bytesToCopy = Math.Min(source.Length - written, bytesLeft);

                _current.EnsureWritable();
                source.Slice(written, bytesToCopy)
                    .CopyTo(new Span<byte>(_current.Array, _current.Offset + _currentPosition, bytesToCopy));

                written += bytesToCopy;

                this.MoveForward(bytesToCopy);

                if (_isEOF) break;
            }

            ENSURE(written == source.Length, "current value must fit inside defined buffer");
        }
    }
}
