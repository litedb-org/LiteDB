using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        private readonly WalChecksum _checksums = new WalChecksum();
        private byte[] _recoveredHeader;
        private readonly DataChecksumPolicy _dataChecksums = new DataChecksumPolicy();

        private void InitializeChecksums()
        {
            var bytes = new byte[PAGE_SIZE];
            _dataStream.Position = 0;
            _dataStream.ReadRequired(bytes, 0, bytes.Length);
            var journal = _logStream == null ? null : HeaderJournal.Read(_logStream);
            if (journal != null)
            {
                var published = journal.IsPublished(bytes);
                journal.ValidateCheckpointWal(_logStream, published ? bytes : journal.Header);
                if (!published) bytes = _recoveredHeader = journal.Header;
                _checksums.JournalBytes = journal.Legacy && published ? _logStream.Length : journal.FooterBytes;
                if (journal.ConfirmsLegacyBackup && !published && journal.FooterBytes != 0)
                    _checksums.LegacyConfirmationPosition = journal.Position - PAGE_SIZE;
            }
            if (bytes[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION &&
                new BufferSlice(bytes, 0, PAGE_SIZE).ReadUInt32(WalChecksum.MarkerPosition) != WalChecksum.HeaderMarker) return;
            // Mixed-page permissions must only come from a verified header.
            var header = new BufferSlice(bytes, 0, PAGE_SIZE);
            PageChecksum.Validate(header, 0);
            _dataChecksums.Load(header);
            var salt = new byte[16];
            Buffer.BlockCopy(bytes, WalChecksum.SaltPosition, salt, 0, salt.Length);
            _checksums.Reset(salt);
            _checksums.Retirement = WalRetirement.Load(header, _logStream, _checksums);
        }

        private void ReadRecoveredHeader(PageBuffer page, uint id, FileOrigin origin)
        {
            if (id == 0 && origin == FileOrigin.Data && _recoveredHeader != null)
                Buffer.BlockCopy(_recoveredHeader, 0, page.Array, page.Offset, PAGE_SIZE);
        }

        private IEnumerable<PageBuffer> ReadWalFrames()
        {
            var stream = (ChecksummedWalStream)_logStream;
            foreach (var page in WalRetirementReader.Read(stream.RawStream, _checksums, stream.Length))
                yield return page;
        }

        private void LoadChecksummedIndexMap()
        {
            var recovery = new WalRecovery();
            var transactions = new Dictionary<uint, List<PagePosition>>();
            foreach (var page in recovery.Read(ReadWalFrames()))
            {
                var id = page.ReadUInt32(BasePage.P_TRANSACTION_ID);
                if (!transactions.TryGetValue(id, out var pages)) transactions[id] = pages = new List<PagePosition>();
                if (!page.WalFrame.Retired) pages.Add(new PagePosition(page.ReadUInt32(BasePage.P_PAGE_ID), page.Position));
                if (!page.ReadBool(BasePage.P_IS_CONFIRMED)) continue;
                foreach (var entry in pages) _logIndexMap[entry.PageID] = entry.Position;
                transactions.Remove(id);
            }
            recovery.RequireRetirement(_checksums.Retirement);
            if (recovery.InvalidTail || ((ChecksummedWalStream)_logStream).TrailingBytes != 0)
                HandleError("Checksum recovery discarded an incomplete WAL tail.",
                    new PageInfo { Origin = FileOrigin.Log, Position = recovery.ConfirmedEnd });
        }
    }
}
