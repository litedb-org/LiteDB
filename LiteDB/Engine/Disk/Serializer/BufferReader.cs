using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Read multiple array segment as a single linear segment - Forward Only
    /// </summary>
    internal partial class BufferReader : IDisposable
    {
        private IEnumerator<BufferSlice> _source;
        private readonly DataService _dataSource;
        private readonly bool _utcDate;

        private PageAddress _nextAddress;
        private ulong _dataBlockCount;

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
        internal bool AllowZeroLengthDocument { get; set; }

        public BufferReader(byte[] buffer, bool utcDate = false)
            : this(new BufferSlice(buffer, 0, buffer.Length), utcDate)
        {
        }

        public BufferReader(BufferSlice buffer, bool utcDate = false)
        {
            _source = null;
            _dataSource = null;
            _utcDate = utcDate;

            _current = buffer;
        }

        public BufferReader(IEnumerable<BufferSlice> source, bool utcDate = false, ArrayPool<byte> bufferPool = null)
        {
            _dataSource = null;
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

        internal BufferReader(DataService source, bool utcDate)
        {
            _source = null;
            _dataSource = source;
            _utcDate = utcDate;
            _current = null;
            _isEOF = true;
        }

        internal void Reset(PageAddress address)
        {
            ENSURE(_dataSource != null, "only a data-service reader can be reset");

            _nextAddress = address;
            _dataBlockCount = 0;
            _currentPosition = 0;
            _position = 0;
            _isEOF = !_dataSource.TryRead(ref _nextAddress, ref _dataBlockCount, out _current);
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
                if (_source != null && _source.MoveNext())
                {
                    _current = _source.Current;
                    _currentPosition = 0;
                }
                else if (_dataSource != null &&
                    _dataSource.TryRead(ref _nextAddress, ref _dataBlockCount, out _current))
                {
                    _currentPosition = 0;
                }
                else
                {
                    _isEOF = true;
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
        
        private T ReadNumber<T>(int size) where T : struct
        {
            if (this.TryGetContiguousSpan(size, out var source))
            {
                var value = MemoryMarshal.Read<T>(source);
                this.MoveForward(size);
                return value;
            }

            Span<byte> scratch = stackalloc byte[8];
            var valueBytes = scratch.Slice(0, size);
            this.Read(valueBytes);

            return MemoryMarshal.Read<T>(valueBytes);
        }

        public Int32 ReadInt32() => this.ReadNumber<Int32>(4);
        public Int64 ReadInt64() => this.ReadNumber<Int64>(8);
        public UInt16 ReadUInt16() => this.ReadNumber<UInt16>(2);
        public UInt32 ReadUInt32() => this.ReadNumber<UInt32>(4);
        public Single ReadSingle() => this.ReadNumber<Single>(4);
        public Double ReadDouble() => this.ReadNumber<Double>(8);

        public Decimal ReadDecimal()
        {
            var a = this.ReadInt32();
            var b = this.ReadInt32();
            var c = this.ReadInt32();
            var d = this.ReadInt32();
            var scale = (byte)((d >> 16) & 0xFF);

            if ((d & 0x7F00FFFF) != 0 || scale > 28)
            {
                throw new ArgumentException("Invalid decimal flags in BSON value.");
            }

            return new Decimal(a, b, c, (d & unchecked((int)0x80000000)) != 0, scale);
        }

        #endregion

        #region Complex Types

        /// <summary>
        /// Read DateTime as UTC ticks (not BSON format)
        /// </summary>
        public DateTime ReadDateTime()
        {
            var date = this.ReadInt64().ToUtcDateTime();

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
                Span<byte> buffer = stackalloc byte[16];

                this.Read(buffer);

                value = BufferSliceExtensions.ReadGuid(buffer);
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
                _current.EnsureReadable();
                value = BufferSliceExtensions.ReadObjectId(new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, 12));

                this.MoveForward(12);
            }
            else
            {
                Span<byte> buffer = stackalloc byte[12];

                this.Read(buffer);

                value = BufferSliceExtensions.ReadObjectId(buffer);
            }

            return value;
        }

        /// <summary>
        /// Write a boolean as 1 byte (0 or 1)
        /// </summary>
        public bool ReadBoolean()
        {
            this.EnsureByteAvailable();
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
            this.EnsureByteAvailable();
            var value = _current[_currentPosition];
            this.MoveForward(1);
            return value;
        }

        private void EnsureByteAvailable()
        {
            while (!_isEOF && _currentPosition == _current.Count) this.MoveForward(0);
            ENSURE(!_isEOF && _currentPosition < _current.Count, "cannot read past end of buffer");
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
            ENSURE(count >= 0 && count <= MAX_DOCUMENT_SIZE, "binary length exceeds the document limit");
            var buffer = new byte[count];
            this.Read(buffer, 0, count);
            return buffer;
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
                if (length == 0 && AllowZeroLengthDocument) return doc;
                ENSURE(length >= 5 && length <= MAX_DOCUMENT_SIZE,
                    "document length must include its header and terminator and stay within the document limit");
                var end = (long)_position + length - 5;
                ENSURE(end <= int.MaxValue, "document length exceeds the supported buffer range");
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

                if (_position < end) this.Skip((int)end - _position);
                ENSURE(_position == end, "document element exceeds its declared container boundary");
                ENSURE(this.ReadByte() == 0, "document must end with a null terminator");

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
                ENSURE(length >= 5 && length <= MAX_DOCUMENT_SIZE,
                    "array length must include its header and terminator and stay within the document limit");
                var end = (long)_position + length - 5;
                ENSURE(end <= int.MaxValue, "array length exceeds the supported buffer range");

                while (_position < end)
                {
                    var value = BsonElementReader.Read(this, null, _utcDate, out string name);
                    arr.Add(value);
                }

                ENSURE(_position == end, "array element exceeds its declared container boundary");
                ENSURE(this.ReadByte() == 0, "array must end with a null terminator");

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
