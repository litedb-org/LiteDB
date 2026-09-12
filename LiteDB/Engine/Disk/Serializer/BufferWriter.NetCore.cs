using System;
using static LiteDB.Constants;

namespace LiteDB.Engine;

internal partial class BufferWriter
{
    private const int StackAllocationThreshold = 256;

    /// <summary>
    /// Write String with \0 at end
    /// </summary>
    public partial void WriteCString(string value)
    {
        if (value.IndexOf('\0') > -1) throw LiteException.InvalidNullCharInString();

        var bytesCount = StringEncoding.UTF8.GetByteCount(value);

        if (this.TryWriteInline(value.AsSpan(), bytesCount, 1))
        {
            this.Write((byte)0x00);
            return;
        }

        if (bytesCount <= StackAllocationThreshold)
        {
            Span<byte> stackBuffer = stackalloc byte[StackAllocationThreshold];
            var buffer = stackBuffer.Slice(0, bytesCount);

            StringEncoding.UTF8.GetBytes(value.AsSpan(), buffer);

            this.WriteSpan(buffer);
        }
        else
        {
            var rented = _bufferPool.Rent(bytesCount);

            try
            {
                var buffer = rented.AsSpan(0, bytesCount);

                StringEncoding.UTF8.GetBytes(value.AsSpan(), buffer);

                this.WriteSpan(buffer);
            }
            finally
            {
                _bufferPool.Return(rented, true);
            }
        }

        this.Write((byte)0x00);
    }

    /// <summary>
    /// Write string into output buffer.
    /// Support direct string (with no length information) or BSON specs: with (legnth + 1) [4 bytes] before and '\0' at end = 5 extra bytes
    /// </summary>
    public partial void WriteString(string value, bool specs)
    {
        var count = StringEncoding.UTF8.GetByteCount(value);

        if (specs)
        {
            this.Write(count + 1); // write Length + 1 (for \0)
        }

        if (this.TryWriteInline(value.AsSpan(), count, specs ? 1 : 0))
        {
            if (specs)
            {
                this.Write((byte)0x00);
            }

            return;
        }

        if (count <= StackAllocationThreshold)
        {
            Span<byte> stackBuffer = stackalloc byte[StackAllocationThreshold];
            var buffer = stackBuffer.Slice(0, count);

            StringEncoding.UTF8.GetBytes(value.AsSpan(), buffer);

            this.WriteSpan(buffer);
        }
        else
        {
            var rented = _bufferPool.Rent(count);

            try
            {
                var buffer = rented.AsSpan(0, count);

                StringEncoding.UTF8.GetBytes(value.AsSpan(), buffer);

                this.WriteSpan(buffer);
            }
            finally
            {
                _bufferPool.Return(rented, true);
            }
        }

        if (specs)
        {
            this.Write((byte)0x00);
        }
    }

    private bool TryWriteInline(ReadOnlySpan<char> chars, int byteCount, int extraBytes)
    {
        var required = byteCount + extraBytes;

        if (required > _current.Count - _currentPosition)
        {
            return false;
        }

        if (byteCount > 0)
        {
            var destination = new Span<byte>(_current.Array, _current.Offset + _currentPosition, byteCount);
            var written = StringEncoding.UTF8.GetBytes(chars, destination);
            ENSURE(written == byteCount, "encoded byte count mismatch");

            this.MoveForward(byteCount);
        }

        return true;
    }

    private void WriteSpan(ReadOnlySpan<byte> source)
    {
        var offset = 0;

        while (offset < source.Length)
        {
            if (_currentPosition == _current.Count)
            {
                this.MoveForward(0);

                ENSURE(_isEOF == false, "current value must fit inside defined buffer");
            }

            var available = _current.Count - _currentPosition;
            var toCopy = Math.Min(source.Length - offset, available);

            var target = new Span<byte>(_current.Array, _current.Offset + _currentPosition, toCopy);
            source.Slice(offset, toCopy).CopyTo(target);

            this.MoveForward(toCopy);
            offset += toCopy;
        }
    }
}

