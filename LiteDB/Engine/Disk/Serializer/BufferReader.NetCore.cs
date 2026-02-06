using System;
using static LiteDB.Constants;

namespace LiteDB.Engine;

internal partial class BufferReader
{
    /// <summary>
    /// Read string with fixed size
    /// </summary>
    public string ReadString(int count)
    {
        if (count == 0)
        {
            return string.Empty;
        }

        // if fits in current segment, use inner array - otherwise copy from multiples segments
        if (_currentPosition + count <= _current.Count)
        {
            var span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, count);
            var value = StringEncoding.UTF8.GetString(span);

            this.MoveForward(count);

            return value;
        }

        const int stackLimit = 256;

        if (count <= stackLimit)
        {
            Span<byte> stackBuffer = stackalloc byte[stackLimit];
            var destination = stackBuffer.Slice(0, count);

            this.ReadIntoSpan(destination);

            return StringEncoding.UTF8.GetString(destination);
        }

        var rented = _bufferPool.Rent(count);

        try
        {
            var destination = rented.AsSpan(0, count);

            this.ReadIntoSpan(destination);

            return StringEncoding.UTF8.GetString(destination);
        }
        finally
        {
            _bufferPool.Return(rented, true);
        }
    }

    private void ReadIntoSpan(Span<byte> destination)
    {
        var offset = 0;

        while (offset < destination.Length)
        {
            if (_currentPosition == _current.Count)
            {
                this.MoveForward(0);

                if (_isEOF)
                {
                    break;
                }
            }

            var available = _current.Count - _currentPosition;
            var toCopy = Math.Min(destination.Length - offset, available);

            var source = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, toCopy);
            source.CopyTo(destination.Slice(offset, toCopy));

            this.MoveForward(toCopy);
            offset += toCopy;
        }

        ENSURE(offset == destination.Length, "current value must fit inside defined buffer");
    }

    /// <summary>
    /// Reading string until find \0 at end
    /// </summary>
    public string ReadCString()
    {
        // first try read CString in current segment
        if (this.TryReadCStringCurrentSegment(out var value))
        {
            return value;
        }
        else
        {
            const int stackLimit = 256;

            Span<byte> stackBuffer = stackalloc byte[stackLimit];
            Span<byte> destination = stackBuffer;
            byte[] rented = null;
            var total = 0;

            while (true)
            {
                if (_currentPosition == _current.Count)
                {
                    this.MoveForward(0);

                    if (_isEOF)
                    {
                        ENSURE(false, "missing null terminator for CString");
                    }

                    continue;
                }

                var available = _current.Count - _currentPosition;

                var span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, available);
                var terminator = span.IndexOf((byte)0x00);
                var take = terminator >= 0 ? terminator : span.Length;

                var required = total + take;

                if (required > destination.Length)
                {
                    var newLength = Math.Max(required, Math.Max(destination.Length * 2, stackLimit * 2));
                    var buffer = _bufferPool.Rent(newLength);

                    destination.Slice(0, total).CopyTo(buffer.AsSpan(0, total));

                    if (rented != null)
                    {
                        _bufferPool.Return(rented, true);
                    }

                    rented = buffer;
                    destination = rented.AsSpan();
                }

                if (take > 0)
                {
                    span.Slice(0, take).CopyTo(destination.Slice(total));
                    total += take;
                    this.MoveForward(take);
                }

                if (terminator >= 0)
                {
                    this.MoveForward(1); // +1 to '\0'
                    break;
                }
            }

            var result = StringEncoding.UTF8.GetString(destination.Slice(0, total));

            if (rented != null)
            {
                _bufferPool.Return(rented, true);
            }

            return result;
        }
    }
}