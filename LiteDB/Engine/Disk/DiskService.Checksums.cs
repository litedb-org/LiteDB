using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private readonly WalChecksum _checksums = new WalChecksum();
        internal bool ChecksumsEnabled => _checksums.Enabled;
        internal long DiscardedWalBytes { get; private set; }

        private void LoadChecksums(BufferSlice header)
        {
            FileVersion = header[HeaderPage.P_FILE_VERSION];
            if (FileVersion != HeaderPage.CHECKSUM_FILE_VERSION && header.ReadUInt32(WalChecksum.MarkerPosition) != WalChecksum.HeaderMarker) return;
            PageChecksum.Validate(header, 0);
            if (FileVersion != HeaderPage.CHECKSUM_FILE_VERSION) throw LiteException.UnsupportedFileVersion(FileVersion);
            var salt = new byte[16];
            Buffer.BlockCopy(header.Array, header.Offset + WalChecksum.SaltPosition, salt, 0, salt.Length);
            _checksums.Reset(salt);
        }

        private void StampDataPage(BufferSlice page)
        {
            if (!ChecksumsEnabled) return;
            if (page.ReadUInt32(BasePage.P_PAGE_ID) == 0)
            {
                Buffer.BlockCopy(_checksums.Salt, 0, page.Array, page.Offset + WalChecksum.SaltPosition, 16);
                page.Write(WalChecksum.HeaderMarker, WalChecksum.MarkerPosition);
            }
            PageChecksum.Write(page);
        }

        private void MarkHeaderInvalid(BufferSlice header)
        {
            if (ChecksumsEnabled) PageChecksum.Validate(header, 0);
            header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            StampDataPage(header);
        }

        /// <summary>
        /// Legacy WAL must be checkpointed first. Checksums occupy bytes ignored by
        /// legacy readers; publish v10 only after every existing data page is synced.
        /// Interrupted conversions can be repeated while the header remains v8/v9.
        /// </summary>
        internal void EnableChecksums(ref HeaderPage header)
        {
            if (_readOnly || ChecksumsEnabled) return;
            var stream = _dataPool.Writer.Value;
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            for (long position = PAGE_SIZE; position < GetFileLength(FileOrigin.Data); position += PAGE_SIZE)
            {
                stream.Position = position;
                stream.ReadRequired(buffer.Array, 0, PAGE_SIZE);
                // Preallocated pages beyond LastPageID are not database pages yet.
                if (position / PAGE_SIZE > header.LastPageID) break;
                PageChecksum.Write(buffer);
                stream.Position = position;
                stream.Write(buffer.Array, 0, PAGE_SIZE);
            }
            stream.FlushToDisk();
            stream.Position = 0;
            stream.ReadRequired(buffer.Array, 0, PAGE_SIZE);
            buffer[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
            _checksums.Reset(Guid.NewGuid().ToByteArray());
            StampDataPage(buffer);
            stream.Position = 0;
            stream.Write(buffer.Array, 0, PAGE_SIZE);
            stream.FlushToDisk();
            FileVersion = HeaderPage.CHECKSUM_FILE_VERSION;
            header.EnsureVersion(FileVersion);
            Buffer.BlockCopy(_checksums.Salt, 0, header.Buffer.Array, header.Buffer.Offset + WalChecksum.SaltPosition, 16);
            _cache.Clear();
        }

        /// <summary>Called only after checkpoint synced all data, before recycling the WAL.</summary>
        internal void RotateWalSalt()
        {
            if (!ChecksumsEnabled) return;
            var stream = _dataPool.Writer.Value;
            var header = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            stream.Position = 0;
            stream.ReadRequired(header.Array, 0, PAGE_SIZE);
            PageChecksum.Validate(header, 0);
            _checksums.Reset(Guid.NewGuid().ToByteArray());
            StampDataPage(header);
            stream.Position = 0;
            stream.Write(header.Array, 0, PAGE_SIZE);
            stream.FlushToDisk();
        }

        internal void DiscardWalTail(long end)
        {
            DiscardedWalBytes += (GetFileLength(FileOrigin.Log) - end) / PAGE_SIZE * WalChecksum.FrameSize + _logTrailingLength;
            LOG($"WAL checksum recovery discarded {DiscardedWalBytes} bytes after logical position {end}", "RECOVERY");
            if (!_readOnly)
            {
                SetLength(end, FileOrigin.Log);
                _writer.Value.FlushToDisk();
            }
            else _logLength = end - PAGE_SIZE;
            _logTrailingLength = 0;
        }

        internal void FinishWalRecovery(WalRecovery recovery)
        {
            if (recovery.InvalidTail || recovery.ConfirmedEnd < GetFileLength(FileOrigin.Log) || _logTrailingLength != 0)
                DiscardWalTail(recovery.ConfirmedEnd);
            _checksums.Recovered(recovery.Sequence, recovery.ConfirmedEnd);
        }

        internal void ForgetWalTransaction(uint transactionID)
        {
            if (!_writer.IsValueCreated) return;
            lock (_writer.Value) _checksums.Forget(transactionID);
        }

        internal void StopAfterCheckpointFailure(Exception exception) => _state.Stop(exception);
    }
}
