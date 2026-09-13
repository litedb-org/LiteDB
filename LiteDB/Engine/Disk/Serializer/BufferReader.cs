using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Read multiple array segment as a single linear segment - Forward Only
    /// </summary>
    internal class BufferReader : IDisposable
    {
        private readonly IEnumerator<BufferSlice> _source;
        private readonly bool _utcDate;

        private BufferSlice _current;
        private int _currentPosition = 0; // position in _current
        private int _position = 0; // global position

        private bool _isEOF = false;

        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        /// <summary>
        /// Current global cursor position
        /// </summary>
        public int Position => _position;

        /// <summary>
        /// Indicate position are at end of last source array segment
        /// </summary>
        public bool IsEOF => _isEOF;

        public BufferReader(byte[] buffer, bool utcDate = false)
            : this(new BufferSlice(buffer, 0, buffer.Length), utcDate)
        {
        }

        public BufferReader(BufferSlice buffer, bool utcDate = false)
        {
            _source = null;
            _utcDate = utcDate;

            _current = buffer;
        }

        public BufferReader(IEnumerable<BufferSlice> source, bool utcDate = false)
        {
            _source = source.GetEnumerator();
            _utcDate = utcDate;

            _source.MoveNext();
            _current = _source.Current;
        }

        #region Basic Read

        /// <summary>
        /// Move forward in current segment. If array segment finishes, open next segment
        /// Returns true if moved to another segment - returns false if continues in the same segment
        /// </summary>
        private bool MoveForward(int count)
        {
            // do not move forward if source finish
            if (_isEOF) return false;

            ENSURE(_currentPosition + count <= _current.Count, "forward is only for current segment");

            _currentPosition += count;
            _position += count;

            // request new source array if _current all consumed
            if (_currentPosition == _current.Count)
            {
                if (_source == null || _source.MoveNext() == false)
                {
                    _isEOF = true;
                }
                else
                {
                    _current = _source.Current;
                    _currentPosition = 0;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Read bytes from source and copy into buffer. Return how many bytes was read
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            var bufferPosition = 0;

            while (bufferPosition < count)
            {
                var bytesLeft = _current.Count - _currentPosition;
                var bytesToCopy = Math.Min(count - bufferPosition, bytesLeft);

                // fill buffer
                if (buffer != null)
                {
                    Buffer.BlockCopy(_current.Array,
                        _current.Offset + _currentPosition,
                        buffer,
                        offset + bufferPosition,
                        bytesToCopy);
                }

                bufferPosition += bytesToCopy;

                // move position in current segment (and go to next segment if finish)
                this.MoveForward(bytesToCopy);

                if (_isEOF) break;
            }

            ENSURE(count == bufferPosition, "current value must fit inside defined buffer");

            return bufferPosition;
        }

        /// <summary>
        /// Skip bytes (same as Read but with no array copy)
        /// </summary>
        public int Skip(int count) => this.Read(null, 0, count);

        /// <summary>
        /// Consume all data source until finish
        /// </summary>
        public void Consume()
        {
            if (_source != null)
            {
                while (_source.MoveNext())
                {
                }
            }
        }

        #endregion

        #region Read String

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

        /// <summary>	
        /// Try read CString in current segment avoind read byte-to-byte over segments	
        /// </summary>	
        private bool TryReadCStringCurrentSegment(out string value)
        {
            var pos = _currentPosition;
            var count = 0;
            while (pos < _current.Count)
            {
                if (_current[pos] == 0x00)
                {
                    value = StringEncoding.UTF8.GetString(_current.Array, _current.Offset + _currentPosition, count);
                    this.MoveForward(count + 1); // +1 means '\0'	
                    return true;
                }
                else
                {
                    count++;
                    pos++;
                }
            }
            value = null;
            return false;
        }

        #endregion

        #region Read Numbers

        private T ReadNumber<T>(Func<byte[], int, T> convert, int size)
        {
            T value;

            // if fits in current segment, use inner array - otherwise copy from multiples segments
            if (_currentPosition + size <= _current.Count)
            {
                value = convert(_current.Array, _current.Offset + _currentPosition);

                this.MoveForward(size);
            }
            else
            {
                var buffer = _bufferPool.Rent(size);

                this.Read(buffer, 0, size);

                value = convert(buffer, 0);

                _bufferPool.Return(buffer, true);
            }

            return value;
        }

        public Int32 ReadInt32() => this.ReadNumber(BitConverter.ToInt32, 4);
        public Int64 ReadInt64() => this.ReadNumber(BitConverter.ToInt64, 8);
        public UInt32 ReadUInt32() => this.ReadNumber(BitConverter.ToUInt32, 4);
        public Double ReadDouble() => this.ReadNumber(BitConverter.ToDouble, 8);

        public Decimal ReadDecimal()
        {
            var a = this.ReadInt32();
            var b = this.ReadInt32();
            var c = this.ReadInt32();
            var d = this.ReadInt32();
            return new Decimal(new int[] { a, b, c, d });
        }

        #endregion

        #region Complex Types

        /// <summary>
        /// Read DateTime as UTC ticks (not BSON format)
        /// </summary>
        public DateTime ReadDateTime()
        {
            var date = new DateTime(this.ReadInt64(), DateTimeKind.Utc);

            return _utcDate ? date.ToLocalTime() : date;
        }

        /// <summary>
        /// Read Guid as 16 bytes array
        /// </summary>
        public Guid ReadGuid()
        {
            Guid value;

            if (_currentPosition + 16 <= _current.Count)
            {
                value = _current.ReadGuid(_currentPosition);

                this.MoveForward(16);
            }
            else
            {
                // can't use _tempoBuffer because Guid validate 16 bytes array length
                value = new Guid(this.ReadBytes(16));
            }

            return value;
        }

        /// <summary>
        /// Write ObjectId as 12 bytes array
        /// </summary>
        public ObjectId ReadObjectId()
        {
            ObjectId value;

            if (_currentPosition + 12 <= _current.Count)
            {
                value = new ObjectId(_current.Array, _current.Offset + _currentPosition);

                this.MoveForward(12);
            }
            else
            {
                var buffer = _bufferPool.Rent(12);

                this.Read(buffer, 0, 12);

                value = new ObjectId(buffer, 0);

                _bufferPool.Return(buffer, true);
            }

            return value;
        }

        /// <summary>
        /// Write a boolean as 1 byte (0 or 1)
        /// </summary>
        public bool ReadBoolean()
        {
            var value = _current[_currentPosition] != 0;
            this.MoveForward(1);
            return value;
        }

        /// <summary>
        /// Write single byte
        /// </summary>
        public byte ReadByte()
        {
            var value = _current[_currentPosition];
            this.MoveForward(1);
            return value;
        }

        /// <summary>
        /// Write PageAddress as PageID, Index
        /// </summary>
        internal PageAddress ReadPageAddress()
        {
            return new PageAddress(this.ReadUInt32(), this.ReadByte());
        }

        /// <summary>
        /// Read byte array - not great because need create new array instance
        /// </summary>
        public byte[] ReadBytes(int count)
        {
            var buffer = new byte[count];
            this.Read(buffer, 0, count);
            return buffer;
        }

        /// <summary>
        /// Read single IndexKey (BsonValue) from buffer. Use +1 length only for string/binary
        /// </summary>
        public BsonValue ReadIndexKey()
        {
            var type = (BsonType)this.ReadByte();

            switch (type)
            {
                case BsonType.Null: return BsonValue.Null;

                case BsonType.Int32: return this.ReadInt32();
                case BsonType.Int64: return this.ReadInt64();
                case BsonType.Double: return this.ReadDouble();
                case BsonType.Decimal: return this.ReadDecimal();

                // Use +1 byte only for length
                case BsonType.String: return this.ReadString(this.ReadByte());

                case BsonType.Document: return this.ReadDocument(null).GetValue();
                case BsonType.Array: return this.ReadArray().GetValue();

                // Use +1 byte only for length
                case BsonType.Binary: return this.ReadBytes(this.ReadByte());
                case BsonType.ObjectId: return this.ReadObjectId();
                case BsonType.Guid: return this.ReadGuid();

                case BsonType.Boolean: return this.ReadBoolean();
                case BsonType.DateTime: return this.ReadDateTime();

                case BsonType.MinValue: return BsonValue.MinValue;
                case BsonType.MaxValue: return BsonValue.MaxValue;

                default: throw new NotImplementedException();
            }
        }

        #endregion

        #region BsonDocument as SPECS

        /// <summary>
        /// Read a BsonDocument from reader
        /// </summary>
        public Result<BsonDocument> ReadDocument(HashSet<string> fields = null)
        {
            var doc = new BsonDocument();

            try
            {
                var length = this.ReadInt32();
                var end = _position + length - 5;
                var remaining = fields == null || fields.Count == 0 ? null : new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

                while (_position < end && (remaining == null || remaining?.Count > 0))
                {
                    var value = this.ReadElement(remaining, out string name);

                    // null value means are not selected field
                    if (value != null)
                    {
                        doc[name] = value;

                        // remove from remaining fields
                        remaining?.Remove(name);
                    }
                }

                this.MoveForward(1); // skip \0 ** can read disk here!

                return doc;
            }
            catch (Exception ex)
            {
                return new Result<BsonDocument>(doc, ex);
            }
        }

        /// <summary>
        /// Read an BsonArray from reader
        /// </summary>
        public Result<BsonArray> ReadArray()
        {
            var arr = new BsonArray();

            try
            {
                var length = this.ReadInt32();
                var end = _position + length - 5;

                while (_position < end)
                {
                    var value = this.ReadElement(null, out string name);
                    arr.Add(value);
                }

                this.MoveForward(1); // skip \0

                return arr;
            }
            catch (Exception ex)
            {
                return new Result<BsonArray>(arr, ex);
            }
        }

        /// <summary>
        /// Reads an element (key-value) from an reader
        /// </summary>
        private BsonValue ReadElement(HashSet<string> remaining, out string name)
        {
            var type = this.ReadByte();
            name = this.ReadCString();

            // check if need skip this element
            if (remaining != null && !remaining.Contains(name))
            {
                // define skip length according type
                var length =
                    (type == 0x0A || type == 0xFF || type == 0x7F) ? 0 : // Null, MinValue, MaxValue
                    (type == 0x08) ? 1 : // Boolean
                    (type == 0x10) ? 4 : // Int
                    (type == 0x01 || type == 0x12 || type == 0x09) ? 8 : // Double, Int64, DateTime
                    (type == 0x07) ? 12 : // ObjectId
                    (type == 0x13) ? 16 : // Decimal
                    (type == 0x02) ? this.ReadInt32() : // String
                    (type == 0x05) ? this.ReadInt32() + 1 : // Binary (+1 for subtype)
                    (type == 0x03 || type == 0x04) ? this.ReadInt32() - 4 : 0; // Document, Array (-4 to Length + zero)

                if (length > 0)
                {
                    this.Skip(length);
                }

                return null;
            }

            if (type == 0x01) // Double
            {
                return this.ReadDouble();
            }
            else if (type == 0x02) // String
            {
                var length = this.ReadInt32();
                var value = this.ReadString(length - 1);
                this.MoveForward(1); // read '\0'
                return value;
            }
            else if (type == 0x03) // Document
            {
                return this.ReadDocument().GetValue();
            }
            else if (type == 0x04) // Array
            {
                return this.ReadArray().GetValue();
            }
            else if (type == 0x05) // Binary
            {
                var length = this.ReadInt32();
                var subType = this.ReadByte();
                var bytes = this.ReadBytes(length);

                switch (subType)
                {
                    case 0x00: return bytes;
                    case 0x04: return new Guid(bytes);
                }
            }
            else if (type == 0x07) // ObjectId
            {
                return this.ReadObjectId();
            }
            else if (type == 0x08) // Boolean
            {
                return this.ReadBoolean();
            }
            else if (type == 0x09) // DateTime
            {
                var ts = this.ReadInt64();

                // catch specific values for MaxValue / MinValue #19
                if (ts == 253402300800000) return DateTime.MaxValue;
                if (ts == -62135596800000) return DateTime.MinValue;

                var date = BsonValue.UnixEpoch.AddMilliseconds(ts);

                return _utcDate ? date : date.ToLocalTime();
            }
            else if (type == 0x0A) // Null
            {
                return BsonValue.Null;
            }
            else if (type == 0x10) // Int32
            {
                return this.ReadInt32();
            }
            else if (type == 0x12) // Int64
            {
                return this.ReadInt64();
            }
            else if (type == 0x13) // Decimal
            {
                return this.ReadDecimal();
            }
            else if (type == 0xFF) // MinKey
            {
                return BsonValue.MinValue;
            }
            else if (type == 0x7F) // MaxKey
            {
                return BsonValue.MaxValue;
            }

            throw new NotSupportedException("BSON type not supported");
        }

        #endregion

        public void Dispose()
        {
            _source?.Dispose();
        }
    }
}