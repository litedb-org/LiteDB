using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    internal sealed class LiteGrouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        private readonly IReadOnlyList<TElement> _items;

        public LiteGrouping(TKey key, IEnumerable<TElement> items)
        {
            Key = key;
            _items = items as IReadOnlyList<TElement> ?? items.ToList();
        }

        public TKey Key { get; }

        public IEnumerator<TElement> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
