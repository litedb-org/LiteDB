using System.Collections.Generic;
using LiteDB.Utils;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        private void ValidateCheckpoint()
        {
            if (!_disk.ChecksumsEnabled) return;
            var recovery = new WalRecovery();
            var confirmed = new HashSet<uint>();
            foreach (var page in recovery.Read(_disk.ReadFull(FileOrigin.Log)))
            {
                if (page.ReadBool(BasePage.P_IS_CONFIRMED) &&
                    !confirmed.Add(page.ReadUInt32(BasePage.P_TRANSACTION_ID)))
                {
                    Reachability.Sometimes("refusal:checkpoint-wal-verification-failed");
                    throw new PageChecksumException(FileOrigin.Log, page.Position);
                }
            }
            recovery.RequireRetirement(_disk.Retirement);
            if (recovery.InvalidTail || !confirmed.SetEquals(_confirmTransactions))
            {
                Reachability.Sometimes("refusal:checkpoint-wal-verification-failed");
                throw new PageChecksumException(FileOrigin.Log, recovery.ConfirmedEnd);
            }
        }
    }
}
