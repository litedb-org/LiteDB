using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private IEnumerable<BsonDocument> SysTransactions()
        {
            foreach (var transaction in _monitor.GetTransactionsSnapshot())
            {
                yield return new BsonDocument
                {
                    ["threadID"] = transaction.ThreadID,
                    ["transactionID"] = (int)transaction.TransactionID,
                    ["startTime"] = transaction.StartTime,
                    ["mode"] = transaction.Mode.ToString(),
                    ["transactionSize"] = transaction.Pages.TransactionSize,
                    ["maxTransactionSize"] = transaction.MaxTransactionSize,
                    ["pagesInLogFile"] = transaction.Pages.DirtyPages.Count,
                    ["newPages"] = transaction.Pages.NewPages.Count,
                    ["deletedPages"] = transaction.Pages.DeletedPages,
                    ["modifiedPages"] = transaction.Snapshots.Select(x => x.GetWritablePages(true, true).Count()).Sum()
                };
            }
        }
    }
}
