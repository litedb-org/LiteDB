using System;
using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Allocation-free equivalent of BsonDocument.GetElements for private storage.
    /// Every BsonDocument constructor owns a Dictionary, including the raw-value constructor.
    /// Keep the canonical _id spelling and position regardless of insertion order/casing.
    /// </summary>
    internal readonly struct CompactDocumentElements
    {
        private readonly BsonDocument _document;
        internal CompactDocumentElements(BsonDocument document) => _document = document;
        public Enumerator GetEnumerator() => new Enumerator(_document);

        internal struct Enumerator
        {
            private Dictionary<string, BsonValue>.Enumerator _items;
            private readonly BsonValue _id;
            private bool _first;
            public KeyValuePair<string, BsonValue> Current { get; private set; }

            internal Enumerator(BsonDocument document)
            {
                _items = ((Dictionary<string, BsonValue>)document.RawValue).GetEnumerator();
                _first = document.TryGetValue("_id", out _id);
                Current = default;
            }

            public bool MoveNext()
            {
                if (_first)
                {
                    _first = false;
                    Current = new KeyValuePair<string, BsonValue>("_id", _id);
                    return true;
                }
                while (_items.MoveNext())
                {
                    var item = _items.Current;
                    if (item.Key.Equals("_id", StringComparison.OrdinalIgnoreCase)) continue;
                    Current = item;
                    return true;
                }
                return false;
            }
        }
    }
}
