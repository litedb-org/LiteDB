using static LiteDB.Constants;

namespace LiteDB.Engine;

internal partial class BufferWriter
{
    /// <summary>
    /// Write String with \0 at end
    /// </summary>
    public partial void WriteCString(string value)
    {
        if (value.IndexOf('\0') > -1) throw LiteException.InvalidNullCharInString();

        var bytesCount = StringEncoding.UTF8.GetByteCount(value);
        var available = _current.Count - _currentPosition; // avaiable in current segment

        // can write direct in current segment (use < because need +1 \0)
        if (bytesCount < available)
        {
            StringEncoding.UTF8.GetBytes(value, 0, value.Length, _current.Array, _current.Offset + _currentPosition);

            _current[_currentPosition + bytesCount] = 0x00;

            this.MoveForward(bytesCount + 1); // +1 to '\0'
        }
        else
        {
            var buffer = _bufferPool.Rent(bytesCount);

            StringEncoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);

            this.Write(buffer, 0, bytesCount);

            _current[_currentPosition] = 0x00;

            this.MoveForward(1);

            _bufferPool.Return(buffer, true);
        }
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

        if (count <= _current.Count - _currentPosition)
        {
            StringEncoding.UTF8.GetBytes(value, 0, value.Length, _current.Array, _current.Offset + _currentPosition);

            this.MoveForward(count);
        }
        else
        {
            // rent a buffer to be re-usable
            var buffer = _bufferPool.Rent(count);

            StringEncoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);

            this.Write(buffer, 0, count);

            _bufferPool.Return(buffer, true);
        }

        if (specs)
        {
            this.Write((byte)0x00);
        }
    }
}

