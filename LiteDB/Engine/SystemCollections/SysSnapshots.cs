using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private IEnumerable<BsonDocument> SysSnapshots()
        {
            foreach (var transaction in _monitor.GetTransactionsSnapshot())
            {
                foreach (var snapshot in transaction.Snapshots)
                {
                    yield return new BsonDocument
                    {
                        ["transactionID"] = (int)transaction.TransactionID,
                        ["collection"] = snapshot.CollectionName,
                        ["mode"] = snapshot.Mode.ToString(),
                        ["readVersion"] = snapshot.ReadVersion,
                        ["pagesInMemory"] = snapshot.LocalPages.Count,
                        ["collectionDirty"] = snapshot.CollectionPage?.IsDirty ?? false
                    };
                }
            }
        }
    }
}
