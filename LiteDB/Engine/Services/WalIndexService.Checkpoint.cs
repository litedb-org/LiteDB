using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        private void ValidateCheckpoint()
        {
            if (!_disk.ChecksumsEnabled) return;

            // The exclusive lock prevents WAL changes between validation and
            // copying. Check the entire batch before touching data: validating
            // frames only while copying could certify a partial transaction if
            // its last frame is damaged. Keep summaries, not transaction pages,
            // so a large checkpoint does not require buffering the entire WAL.
            var recovery = new WalRecovery();
            var confirmed = new HashSet<uint>();
            foreach (var page in recovery.Read(_disk.ReadFull(FileOrigin.Log)))
            {
                if (page.ReadBool(BasePage.P_IS_CONFIRMED) &&
                    !confirmed.Add(page.ReadUInt32(BasePage.P_TRANSACTION_ID)))
                    throw new PageChecksumException(FileOrigin.Log, page.Position);
            }
            // A missing final confirmation is a valid uncommitted recovery tail,
            // but it must not satisfy an already-acknowledged live transaction.
            if (recovery.InvalidTail || !confirmed.SetEquals(_confirmTransactions))
                throw new PageChecksumException(FileOrigin.Log, recovery.ConfirmedEnd);
        }
    }
}
