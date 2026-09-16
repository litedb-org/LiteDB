using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal sealed class IndexAggregatePipe : BasePipe
    {
        internal IndexAggregatePipe(TransactionService transaction, SortDisk tempDisk, EnginePragmas pragmas, uint maxItemsCount)
            : base(transaction, null, tempDisk, pragmas, maxItemsCount)
        {
        }

        public override IEnumerable<BsonDocument> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query)
        {
            var count = 0;
            var offset = query.Offset;
            if (query.Limit > 0)
            {
                // Index.Run already deduplicates multikey entries by document address.
                // Keep that stream and transaction safepoints, avoiding BSON lookups.
                foreach (var node in nodes)
                {
                    if (offset > 0) offset--;
                    else count = checked(count + 1); // Match COUNT's Int32 overflow behavior.
                    _transaction.Safepoint();
                    if (count > 0 && (!query.RowAggregate.NeedsCount ||
                        (query.Limit < int.MaxValue && count >= query.Limit))) break;
                }
            }
            yield return query.RowAggregate.Project(count);
        }
    }
}
