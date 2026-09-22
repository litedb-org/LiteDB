using System.Collections.Generic;

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
                    throw new PageChecksumException(FileOrigin.Log, page.Position);
            }
            recovery.RequireRetirement(_disk.Retirement);
            if (recovery.InvalidTail || !confirmed.SetEquals(_confirmTransactions))
                throw new PageChecksumException(FileOrigin.Log, recovery.ConfirmedEnd);
        }
    }
}
