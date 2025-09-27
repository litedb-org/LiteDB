using LiteDB.Engine;
using System.Linq;

namespace LiteDB
{
    /// <summary>
    /// Represents a grouped queryable stage that exposes <see cref="IGrouping{TKey, TElement}"/> in LINQ projections.
    /// </summary>
    public class LiteGroupedQueryable<TKey, TElement> : LiteQueryable<IGrouping<TKey, TElement>>
    {
        internal LiteGroupedQueryable(ILiteEngine engine, BsonMapper mapper, string collection, Query query)
            : base(engine, mapper, collection, query)
        {
        }
    }
}
