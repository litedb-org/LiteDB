using System;
using System.Buffers;
using System.IO;
using LiteDB.Utils;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Maps logical 8 KiB pages to plaintext pages plus a 64-byte trailer.</summary>
    internal sealed class ChecksummedWalStream : Stream
    {
        private readonly Stream _stream;
        private readonly WalChecksum _checksum;
        private readonly byte[] _frame = new byte[WalChecksum.FrameSize];
        private readonly WalFileAllocation _allocation = new WalFileAllocation();
        private long _position;
        internal WalChecksum.Frame LastFrame { get; private set; }
        internal Stream RawStream => _stream;
        private long ContentLength => Math.Max(0, _stream.Length - _checksum.JournalBytes);
        internal long TrailingBytes => _checksum.Enabled ? WalPadding.TrailingBytes(ContentLength) : ContentLength % PAGE_SIZE;

        internal ChecksummedWalStream(Stream stream, WalChecksum checksum)
        {
            _stream = stream;
            _checksum = checksum;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _checksum.Enabled ? ContentLength / WalChecksum.FrameSize * PAGE_SIZE : ContentLength;
        public override long Position { get => _checksum.Enabled ? _position : _stream.Position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_checksum.Enabled)
            {
                var start = _stream.Position;
                var length = _stream.Read(buffer, offset, count);
                var confirmation = _checksum.LegacyConfirmationPosition + BasePage.P_IS_CONFIRMED;
                if (_checksum.LegacyConfirmationPosition >= 0 && start <= confirmation && confirmation < start + length)
                    buffer[offset + (int)(confirmation - start)] = 1;
                return length;
            }
            CheckPage(count);
            _stream.Position = _position / PAGE_SIZE * WalChecksum.FrameSize;
            var read = _stream.ReadFully(_frame, 0, _frame.Length);
            if (read == 0) return 0;
            if (read != _frame.Length) throw new PageChecksumException(FileOrigin.Log, _position);
            var page = new BufferSlice(_frame, 0, PAGE_SIZE);
            LastFrame = _checksum.Validate(page, new BufferSlice(_frame, PAGE_SIZE, WalChecksum.MetadataSize), _position);
            Buffer.BlockCopy(_frame, 0, buffer, offset, PAGE_SIZE);
            _position += PAGE_SIZE;
            return PAGE_SIZE;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_checksum.JournalBytes != 0)
            {
                Reachability.Sometimes("refusal:wal-append-during-header-publication");
                throw new IOException("Cannot append WAL pages during header publication.");
            }
            if (!_checksum.Enabled)
            {
                _stream.Write(buffer, offset, count);
                return;
            }
            CheckPage(count);
            Buffer.BlockCopy(buffer, offset, _frame, 0, PAGE_SIZE);
            var page = new BufferSlice(_frame, 0, PAGE_SIZE);
            var frame = _checksum.Prepare(page, new BufferSlice(_frame, PAGE_SIZE, WalChecksum.MetadataSize), _position);
            _stream.Position = _position / PAGE_SIZE * WalChecksum.FrameSize;
            _allocation.Reserve(_stream, _stream.Position + _frame.Length);
            _stream.Write(_frame, 0, _frame.Length);
            _checksum.Accept(page, _position, frame);
            _position += PAGE_SIZE;
        }

        /// <summary>
        /// Writes a final appended checksummed frame and its compatibility
        /// zeros in one bounded physical write. This is deliberately limited
        /// to the engine's plain FileStream WAL; wrappers and caller streams
        /// retain the ordinary frame-then-pad path.
        /// </summary>
        internal void WriteFinalPadded(byte[] buffer, int offset)
        {
            if (!_checksum.Enabled || _checksum.JournalBytes != 0 || _stream.GetType() != typeof(FileStream))
            {
                Write(buffer, offset, PAGE_SIZE);
                return;
            }

            CheckPage(PAGE_SIZE);
            Buffer.BlockCopy(buffer, offset, _frame, 0, PAGE_SIZE);
            var page = new BufferSlice(_frame, 0, PAGE_SIZE);
            var frame = _checksum.Prepare(page,
                new BufferSlice(_frame, PAGE_SIZE, WalChecksum.MetadataSize), _position);
            var physical = checked(_position / PAGE_SIZE * WalChecksum.FrameSize);
            var end = checked(physical + _frame.Length);
            var aligned = WalPadding.AlignedLength(end);
            var count = checked((int)(aligned - physical));
            var combined = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                Buffer.BlockCopy(_frame, 0, combined, 0, _frame.Length);
                Array.Clear(combined, _frame.Length, count - _frame.Length);
                _stream.Position = physical;
                _allocation.Reserve(_stream, aligned);
                _stream.Write(combined, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(combined, clearArray: true);
            }
            _checksum.Accept(page, _position, frame);
            _position += PAGE_SIZE;
        }

        private void CheckPage(int count)
        {
            if (count != PAGE_SIZE || _position % PAGE_SIZE != 0)
                throw new ArgumentException("Checksummed WAL I/O requires one aligned page.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_checksum.Enabled) return _stream.Seek(offset, origin);
            var position = origin == SeekOrigin.Begin ? offset :
                checked((origin == SeekOrigin.Current ? _position : Length) + offset);
            if (position < 0) throw new IOException("Cannot seek before the WAL.");
            return _position = position;
        }

        public override void SetLength(long value)
        {
            if (_checksum.Enabled && value % PAGE_SIZE != 0) throw new ArgumentException("WAL length must be page aligned.");
            var length = _checksum.Enabled ? checked(value / PAGE_SIZE * WalChecksum.FrameSize) : value;
            _stream.SetLength(_checksum.Enabled ? WalPadding.AlignedLength(length) : length);
            _allocation.Reset();
            // CryptoStream can forward an empty final write on dispose. Do not
            // let a borrowed stream position re-extend a truncated memory WAL.
            if (_stream.Position > length) _stream.Position = length;
            if (_position > value) _position = value;
            _checksum.JournalBytes = 0;
            _checksum.LegacyConfirmationPosition = -1;
        }

        private void Pad()
        {
            if (_checksum.Enabled && _checksum.JournalBytes == 0)
                WalPadding.Pad(_stream, Length / PAGE_SIZE * WalChecksum.FrameSize);
        }

        public override void Flush() { Pad(); _stream.Flush(); }
        internal void FlushToDisk() { Pad(); _stream.FlushToDisk(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}
