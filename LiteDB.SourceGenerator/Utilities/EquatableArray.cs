using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.SourceGenerator.Utilities;

internal readonly struct EquatableArray<T> : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items)
    {
        _items = items.ToArray();
    }

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var comparer = EqualityComparer<T>.Default;
            var hash = 17;
            foreach (var item in Items)
            {
                hash = (hash * 31) ^ comparer.GetHashCode(item);
            }

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

    private T[] Items => _items ?? Array.Empty<T>();
}
