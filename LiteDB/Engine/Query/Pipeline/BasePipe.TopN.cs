using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal abstract partial class BasePipe
    {
        private IEnumerable<BsonDocument> OrderTopN(IEnumerable<BsonDocument> source, OrderBy orderBy, int offset, int limit)
        {
            var segments = orderBy.Segments;
            var orders = segments.Select(x => x.Order).ToArray();
            var sorter = new TopNSort(offset + limit, _pragmas.Collation, segments.Count == 1 ? orders[0] : Query.Ascending);
            foreach (var doc in source)
            {
                BsonValue key;
                if (segments.Count == 1) key = segments[0].ExecuteScalar(doc, _pragmas.Collation);
                else
                {
                    var values = new BsonValue[segments.Count];
                    for (var i = 0; i < values.Length; i++) values[i] = segments[i].ExecuteScalar(doc, _pragmas.Collation);
                    key = SortKey.FromValues(values, orders);
                }
                sorter.Add(key, doc.RawId);
            }
            foreach (var address in sorter.GetAddresses(offset))
            {
                yield return _lookup.Load(address);
                _transaction.Safepoint();
            }
        }
    }
}
