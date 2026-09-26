using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        internal SharedWriterResume CaptureWriterResume(HeaderPage header, int readVersion)
        {
            var end = GetFileLength(FileOrigin.Log);
            if (_readOnly || !ChecksumsEnabled || end == 0 || end > SharedWriterResume.MaximumWalBytes ||
                readVersion <= 0 || _checksums.JournalBytes != 0 || _logTrailingLength != 0 ||
                _lastLogPositions.Count > SharedWriterResume.MaximumEntries ||
                _freeLogPositions.Count > SharedWriterResume.MaximumEntries) return null;
            // Retirement root records may follow the final commit. Validate that the
            // suffix contains only those records, never an abandoned transaction tail.
            var confirmationPosition = ((long)readVersion - 1) * PAGE_SIZE;
            if (ReadLogFrom(confirmationPosition + PAGE_SIZE).Any()) return null;
            var confirmation = ReadLogFrom(confirmationPosition).FirstOrDefault(page => !page.WalFrame.Retired);
            if (confirmation == null || confirmation.Position != confirmationPosition || !confirmation.ReadBool(BasePage.P_IS_CONFIRMED) ||
                confirmation.WalFrame.Retired || confirmation.WalFrame.Sequence != _checksums.Sequence) return null;
            header.UpdateBuffer();
            return new SharedWriterResume
            {
                FileHeader = CopyResumePage(ReadFull(FileOrigin.Data).First()),
                LogicalHeader = CopyResumePage(header.Buffer),
                Confirmation = CopyResumePage(confirmation),
                End = end,
                ConfirmationPosition = confirmationPosition,
                Sequence = _checksums.Sequence,
                ConfirmationCount = confirmation.WalFrame.Count,
                ConfirmationDigest = confirmation.WalFrame.Digest,
                LastLogPositions = new Dictionary<uint, long>(_lastLogPositions),
                FreeLogPositions = new SortedSet<long>(_freeLogPositions),
                LastWalTransactionID = _lastWalTransactionID
            };
        }

        // Called only under writer ownership, after the fresh disk constructor has
        // independently checked header/journal/salt/retirement and before index import.
        internal bool ValidateWriterResume(SharedWriterResume saved, HeaderPage fileHeader)
        {
            if (_readOnly || !ChecksumsEnabled || _checksums.JournalBytes != 0 ||
                saved.End > GetFileLength(FileOrigin.Log) || !EqualResumePage(saved.FileHeader, fileHeader.Buffer)) return false;
            try
            {
                var confirmation = ReadLogFrom(saved.ConfirmationPosition).FirstOrDefault(page => !page.WalFrame.Retired);
                return confirmation != null && confirmation.Position == saved.ConfirmationPosition && !confirmation.WalFrame.Retired &&
                    confirmation.WalFrame.Sequence == saved.Sequence &&
                    confirmation.WalFrame.Count == saved.ConfirmationCount &&
                    confirmation.WalFrame.Digest == saved.ConfirmationDigest &&
                    EqualResumePage(saved.Confirmation, confirmation);
            }
            catch (PageChecksumException) { return false; } // Full recovery diagnoses the entire WAL.
        }

        internal void ImportWriterResume(SharedWriterResume saved)
        {
            _lastLogPositions = saved.LastLogPositions;
            _freeLogPositions = saved.FreeLogPositions;
            _lastWalTransactionID = saved.LastWalTransactionID;
        }

        private static byte[] CopyResumePage(BufferSlice buffer)
        {
            var bytes = new byte[PAGE_SIZE];
            Buffer.BlockCopy(buffer.Array, buffer.Offset, bytes, 0, PAGE_SIZE);
            return bytes;
        }

        private static bool EqualResumePage(byte[] bytes, BufferSlice page)
        {
            for (var i = 0; i < PAGE_SIZE; i++) if (bytes[i] != page[i]) return false;
            return true;
        }
    }
}
