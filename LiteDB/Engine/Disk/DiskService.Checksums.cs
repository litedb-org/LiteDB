using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private readonly WalChecksum _checksums = new WalChecksum();
        internal bool ChecksumsEnabled => _checksums.Enabled;
        private readonly DataChecksumPolicy _dataChecksums = new DataChecksumPolicy();
        internal string ChecksumCoverage => ChecksumsEnabled ? (_dataChecksums.Mixed ? "Mixed" : "Complete") : "Legacy";
        internal uint LegacyLastPageID => _dataChecksums.LegacyLastPageID;
        internal WalRecoveryReport RecoveryReport { get; private set; }

        private void LoadChecksums(BufferSlice header)
        {
            FileVersion = header[HeaderPage.P_FILE_VERSION];
            if ((FileVersion < HeaderPage.CHECKSUM_FILE_VERSION || FileVersion > HeaderPage.CURRENT_FILE_VERSION) && header.ReadUInt32(WalChecksum.MarkerPosition) != WalChecksum.HeaderMarker) return;
            PageChecksum.Validate(header, 0);
            if (FileVersion < HeaderPage.CHECKSUM_FILE_VERSION || FileVersion > HeaderPage.CURRENT_FILE_VERSION) throw LiteException.UnsupportedFileVersion(FileVersion);
            _dataChecksums.Load(header);
            var salt = new byte[16];
            Buffer.BlockCopy(header.Array, header.Offset + WalChecksum.SaltPosition, salt, 0, salt.Length);
            _checksums.Reset(salt);
            if (FileVersion >= HeaderPage.MVCC_FILE_VERSION && header.ReadInt64(WalRetirement.RootPosition) != 0)
            {
                var reader = (ChecksummedWalStream)_logPool.Rent();
                try { _checksums.Retirement = WalRetirement.Load(header, reader.RawStream, _checksums); }
                finally { _logPool.Return(reader); }
            }
            else _checksums.Retirement = WalRetirement.Load(header, null, _checksums);
        }

        private void StampDataPage(BufferSlice page)
        {
            if (!ChecksumsEnabled) return;
            if (page.ReadUInt32(BasePage.P_PAGE_ID) == 0)
            {
                Buffer.BlockCopy(_checksums.Salt, 0, page.Array, page.Offset + WalChecksum.SaltPosition, 16);
                page.Write(WalChecksum.HeaderMarker, WalChecksum.MarkerPosition);
                _dataChecksums.Write(page);
                _checksums.Retirement.WriteHeader(page);
            }
            PageChecksum.Write(page);
        }

        private void MarkHeaderInvalid(BufferSlice header)
        {
            if (ChecksumsEnabled)
            {
                PageChecksum.Validate(header, 0);
                var original = new byte[PAGE_SIZE];
                Buffer.BlockCopy(header.Array, header.Offset, original, 0, PAGE_SIZE);
                BeginHeaderJournal(original);
            }
            header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            StampDataPage(header);
        }

        /// <summary>
        /// Drain legacy WAL first, then publish v10 with mixed page coverage.
        /// Only the header is overwritten; a bounded, synced header backup
        /// protects publication, including torn encrypted header writes.
        /// </summary>
        internal void EnableChecksums(ref HeaderPage header)
        {
            if (_readOnly || ChecksumsEnabled) return;
            var stream = _dataPool.Writer.Value;
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            stream.Position = 0;
            stream.ReadRequired(buffer.Array, 0, PAGE_SIZE);
            var log = ((ChecksummedWalStream)_writer.Value).RawStream;
            // Successful syncs are required before crossing the format boundary.
            stream.FlushToDisk();
            log.FlushToDisk();
            HeaderJournal.BackupLegacyHeader(log, buffer.Array);
            BeginHeaderJournal(buffer.Array, conversion: true);
            buffer[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
            _dataChecksums.InitializeMixed(header.LastPageID);
            _checksums.Reset(Guid.NewGuid().ToByteArray());
            StampDataPage(buffer);
            stream.Position = 0;
            stream.Write(buffer.Array, 0, PAGE_SIZE);
            stream.FlushToDisk();
            SetLength(0, FileOrigin.Log);
            log.FlushToDisk();
            _recoveredHeader = null;
            FileVersion = HeaderPage.CHECKSUM_FILE_VERSION;
            header = new HeaderPage(buffer);
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

        internal void DiscardWalTail(long end, bool invalidTail)
        {
            var bytes = (GetFileLength(FileOrigin.Log) - end) / PAGE_SIZE * WalChecksum.FrameSize + _logTrailingLength;
            RecoveryReport = new WalRecoveryReport(bytes, invalidTail);
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
            recovery.RequireRetirement(_checksums.Retirement);
            if (recovery.InvalidTail || recovery.ConfirmedEnd < GetFileLength(FileOrigin.Log) || _logTrailingLength != 0)
                DiscardWalTail(recovery.ConfirmedEnd, recovery.InvalidTail || _logTrailingLength != 0);
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
