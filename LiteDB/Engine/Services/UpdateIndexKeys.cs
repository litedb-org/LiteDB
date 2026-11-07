using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Operation-local key snapshots. The common single-secondary-key update needs
    /// no backing array; multikey/multiple-index documents spill into a compact list.
    /// Never retain IndexNode instances across index mutation.
    /// </summary>
    internal struct UpdateIndexKeys
    {
        private (byte Slot, BsonValue Key, PageAddress Position, string Name) _first;
        private List<(byte Slot, BsonValue Key, PageAddress Position, string Name)> _rest;
        public int Count { get; private set; }

        public (byte Slot, BsonValue Key, PageAddress Position, string Name) this[int index] =>
            index == 0 ? _first : _rest[index - 1];

        public void Add(byte slot, BsonValue key, PageAddress position, string name)
        {
            var entry = (slot, key, position, name);
            if (Count == 0)
            {
                _first = entry;
            }
            else
            {
                if (_rest == null)
                {
                    _rest = new List<(byte Slot, BsonValue Key, PageAddress Position, string Name)>();
                }
                _rest.Add(entry);
            }
            Count++;
        }

        public bool Contains(byte slot, BsonValue key)
        {
            for (var i = 0; i < Count; i++)
            {
                var entry = this[i];
                // Deliberately identical to the previous Tuple/Any membership test.
                if (entry.Slot == slot && entry.Key == key) return true;
            }
            return false;
        }
    }
}
