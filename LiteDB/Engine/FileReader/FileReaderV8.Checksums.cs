using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        private readonly WalChecksum _checksums = new WalChecksum();
        private byte[] _recoveredHeader;

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
            if (bytes[HeaderPage.P_FILE_VERSION] != HeaderPage.CHECKSUM_FILE_VERSION &&
                new BufferSlice(bytes, 0, PAGE_SIZE).ReadUInt32(WalChecksum.MarkerPosition) != WalChecksum.HeaderMarker) return;
            // Salvage still reads other intact pages if the header is damaged;
            // ReadPage validates its checksum when consuming its actual fields.
            var salt = new byte[16];
            Buffer.BlockCopy(bytes, WalChecksum.SaltPosition, salt, 0, salt.Length);
            _checksums.Reset(salt);
        }

        private void ReadRecoveredHeader(PageBuffer page, uint id, FileOrigin origin)
        {
            if (id == 0 && origin == FileOrigin.Data && _recoveredHeader != null)
                Buffer.BlockCopy(_recoveredHeader, 0, page.Array, page.Offset, PAGE_SIZE);
        }

        private IEnumerable<PageBuffer> ReadWalFrames()
        {
            var stream = (ChecksummedWalStream)_logStream;
            var bytes = new byte[PAGE_SIZE];
            for (long position = 0; position < stream.Length; position += PAGE_SIZE)
            {
                stream.Position = position;
                stream.ReadRequired(bytes, 0, bytes.Length);
                yield return new PageBuffer(bytes, 0, 0)
                {
                    Position = position, Origin = FileOrigin.Log, WalFrame = stream.LastFrame
                };
            }
        }

        private void LoadChecksummedIndexMap()
        {
            var recovery = new WalRecovery();
            var transactions = new Dictionary<uint, List<PagePosition>>();
            foreach (var page in recovery.Read(ReadWalFrames()))
            {
                var id = page.ReadUInt32(BasePage.P_TRANSACTION_ID);
                if (!transactions.TryGetValue(id, out var pages)) transactions[id] = pages = new List<PagePosition>();
                pages.Add(new PagePosition(page.ReadUInt32(BasePage.P_PAGE_ID), page.Position));
                if (!page.ReadBool(BasePage.P_IS_CONFIRMED)) continue;
                foreach (var entry in pages) _logIndexMap[entry.PageID] = entry.Position;
                transactions.Remove(id);
            }
            if (recovery.InvalidTail || ((ChecksummedWalStream)_logStream).TrailingBytes != 0)
                HandleError("Checksum recovery discarded an incomplete WAL tail.",
                    new PageInfo { Origin = FileOrigin.Log, Position = recovery.ConfirmedEnd });
        }
    }
}
