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
    internal partial class BufferReader : IDisposable
    {
        private IEnumerator<BufferSlice> _source;
        private readonly bool _utcDate;

        private BufferSlice _current;
        private int _currentPosition = 0; // position in _current
        private int _position = 0; // global position

        private bool _isEOF = false;

        private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

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

        public BufferReader(IEnumerable<BufferSlice> source, bool utcDate = false, ArrayPool<byte> bufferPool = null)
        {
            _bufferPool = bufferPool ?? ArrayPool<byte>.Shared;
            _source = source.GetEnumerator();
            _utcDate = utcDate;

            try
            {
                _source.MoveNext();
                _current = _source.Current;
            }
            catch
            {
                _source.Dispose();
                throw;
            }
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
                try
                {
                    this.Read(buffer, 0, size);

                    value = convert(buffer, 0);
                }
                finally
                {
                    _bufferPool.Return(buffer, true);
                }
            }

            return value;
        }

        public Int32 ReadInt32() => this.ReadNumber(BitConverter.ToInt32, 4);
        public Int64 ReadInt64() => this.ReadNumber(BitConverter.ToInt64, 8);
        public UInt16 ReadUInt16() => this.ReadNumber(BitConverter.ToUInt16, 2);
        public UInt32 ReadUInt32() => this.ReadNumber(BitConverter.ToUInt32, 4);
        public Single ReadSingle() => this.ReadNumber(BitConverter.ToSingle, 4);
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
                try
                {
                    this.Read(buffer, 0, 12);

                    value = new ObjectId(buffer, 0);
                }
                finally
                {
                    _bufferPool.Return(buffer, true);
                }
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

        internal BsonValue ReadVector()
        {
            var length = this.ReadUInt16();
            var values = new float[length];

            for (var i = 0; i < length; i++)
            {
                values[i] = this.ReadSingle();
            }

            return new BsonVector(values);
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

                case BsonType.Vector: return this.ReadVector();

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
                    var value = BsonElementReader.Read(this, remaining, _utcDate, out string name);

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
                    var value = BsonElementReader.Read(this, null, _utcDate, out string name);
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


        #endregion

        public void Dispose()
        {
            var source = _source;
            _source = null;
            _current = null;
            source?.Dispose();
        }
    }
}
