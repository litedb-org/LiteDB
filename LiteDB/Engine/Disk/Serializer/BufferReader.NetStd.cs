using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine;

internal partial class BufferReader
{
    /// <summary>
    /// Read string with fixed size
    /// </summary>
    public string ReadString(int count)
    {
        string value;

        // if fits in current segment, use inner array - otherwise copy from multiples segments
        if (_currentPosition + count <= _current.Count)
        {
            value = StringEncoding.UTF8.GetString(_current.Array, _current.Offset + _currentPosition, count);

            this.MoveForward(count);
        }
        else
        {
            // rent a buffer to be re-usable
            var buffer = _bufferPool.Rent(count);

            this.Read(buffer, 0, count);

            value = StringEncoding.UTF8.GetString(buffer, 0, count);

            _bufferPool.Return(buffer, true);
        }

        return value;
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
            using (var mem = new MemoryStream())
            {
                // copy all first segment 
                var initialCount = _current.Count - _currentPosition;

                mem.Write(_current.Array, _current.Offset + _currentPosition, initialCount);

                this.MoveForward(initialCount);

                // and go to next segment
                while (_current[_currentPosition] != 0x00 && _isEOF == false)
                {
                    mem.WriteByte(_current[_currentPosition]);

                    this.MoveForward(1);
                }

                this.MoveForward(1); // +1 to '\0'

                return StringEncoding.UTF8.GetString(mem.ToArray());
            }
        }
    }
}