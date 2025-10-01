using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    internal static class LiteGroupingFieldNames
    {
        public const string Key = "key";
        public const string Items = "items";
    }

    /// <summary>
    /// Concrete <see cref="IGrouping{TKey, TElement}"/> implementation used to materialize
    /// grouping results coming from query execution.
    /// </summary>
    internal sealed class LiteGrouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        internal const string KeyField = LiteGroupingFieldNames.Key;
        internal const string ItemsField = LiteGroupingFieldNames.Items;

        private readonly IReadOnlyList<TElement> _items;

        public LiteGrouping(TKey key, IReadOnlyList<TElement> items)
        {
            Key = key;
            _items = items;
        }

        public TKey Key { get; }

        public IEnumerator<TElement> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
