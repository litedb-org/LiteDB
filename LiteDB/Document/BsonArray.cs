using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public class BsonArray : BsonValue, IList<BsonValue>
    {
        public BsonArray()
            : base(BsonType.Array, new List<BsonValue>())
        {
        }

        public BsonArray(List<BsonValue> array)
            : this()
        {
            if (array == null) throw new ArgumentNullException(nameof(array));

            this.AddRange(array);
        }

        public BsonArray(params BsonValue[] array)
            : this()
        {
            if (array == null) throw new ArgumentNullException(nameof(array));

            this.AddRange(array);
        }

        public BsonArray(IEnumerable<BsonValue> items)
            : this()
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            this.AddRange(items);
        }
        
        public BsonArray(BsonArray items)
            : this()
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            this.AddRange(items);
        }

        public new IList<BsonValue> RawValue => (IList<BsonValue>)base.RawValue;

        public override BsonValue this[int index]
        {
            get
            {
                return this.RawValue[index];
            }
            set
            {
                this.RawValue[index] = value ?? BsonValue.Null;
            }
        }

        public int Count => this.RawValue.Count;

        public bool IsReadOnly => false;

        public void Add(BsonValue item) => this.RawValue.Add(item ?? BsonValue.Null);

        public void AddRange<TCollection>(TCollection collection)
            where TCollection : ICollection<BsonValue>
        {
            if(collection == null)
                throw new ArgumentNullException(nameof(collection));

            var list = (List<BsonValue>)base.RawValue;

            var listEmptySpace = list.Capacity - list.Count;
            if (listEmptySpace < collection.Count)
            {
                list.Capacity += collection.Count;
            }

            foreach (var bsonValue in collection)
            {
                list.Add(bsonValue ?? Null);
            }
        }
        
        public void AddRange(IEnumerable<BsonValue> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            foreach (var item in items)
            {
                this.Add(item ?? BsonValue.Null);
            }
        }

        public void Clear() => this.RawValue.Clear();

        public bool Contains(BsonValue item) => this.RawValue.Contains(item ?? BsonValue.Null);

        public void CopyTo(BsonValue[] array, int arrayIndex) => this.RawValue.CopyTo(array, arrayIndex);

        public IEnumerator<BsonValue> GetEnumerator() => this.RawValue.GetEnumerator();

        public int IndexOf(BsonValue item) => this.RawValue.IndexOf(item ?? BsonValue.Null);

        public void Insert(int index, BsonValue item) => this.RawValue.Insert(index, item ?? BsonValue.Null);

        public bool Remove(BsonValue item) => this.RawValue.Remove(item);

        public void RemoveAt(int index) => this.RawValue.RemoveAt(index);

        IEnumerator IEnumerable.GetEnumerator()
        {
            foreach (var value in this.RawValue)
            {
                yield return value;
            }
        }

        public override int CompareTo(BsonValue other)
        {
            // if types are different, returns sort type order
            if (other.Type != BsonType.Array) return this.Type.CompareTo(other.Type);

            var otherArray = other.AsArray;

            var result = 0;
            var i = 0;
            var stop = Math.Min(this.Count, otherArray.Count);

            // compare each element
            for (; 0 == result && i < stop; i++)
                result = this[i].CompareTo(otherArray[i]);

            if (result != 0) return result;
            if (i == this.Count) return i == otherArray.Count ? 0 : -1;
            return 1;
        }

        private int _length;

        internal override int GetBytesCount(bool recalc)
        {
            if (recalc == false && _length > 0) return _length;

            var length = 5;
            var array = this.RawValue;
            
            for (var i = 0; i < array.Count; i++)
            {
                length += this.GetBytesCountElement(i.ToString(), array[i]);
            }

            return _length = length;
        }
    }
}