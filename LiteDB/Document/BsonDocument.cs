using LiteDB.Engine;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents a BSON document as a dictionary of key-value pairs where keys are strings and values are <see cref="BsonValue"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BsonDocument"/> is the primary type for representing documents in LiteDB. It implements <see cref="IDictionary{TKey, TValue}"/>
    /// and uses case-insensitive string keys.
    /// </para>
    /// <para>
    /// Documents can contain any BSON value type, including nested documents and arrays. The <c>_id</c> field is treated specially
    /// and will always be returned first when enumerating elements via <see cref="GetElements"/>.
    /// </para>
    /// </remarks>
    public class BsonDocument : BsonValue, IDictionary<string, BsonValue>
    {
        public BsonDocument()
            : base(BsonType.Document, new Dictionary<string, BsonValue>(StringComparer.OrdinalIgnoreCase))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonDocument"/> class by copying elements from a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
        /// </summary>
        /// <param name="dict">The concurrent dictionary containing the initial elements.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="dict"/> is <see langword="null"/>.</exception>
        public BsonDocument(ConcurrentDictionary<string, BsonValue> dict)
            : this()
        {
            if (dict == null) throw new ArgumentNullException(nameof(dict));

            foreach(var element in dict)
            {
                this.Add(element);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonDocument"/> class by copying elements from a dictionary.
        /// </summary>
        /// <param name="dict">The dictionary containing the initial elements.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="dict"/> is <see langword="null"/>.</exception>
        public BsonDocument(IDictionary<string, BsonValue> dict)
            : this()
        {
            if (dict == null) throw new ArgumentNullException(nameof(dict));

            foreach (var element in dict)
            {
                this.Add(element);
            }
        }

        /// <summary>
        /// Gets the underlying dictionary containing the document's key-value pairs.
        /// </summary>
        public new IDictionary<string, BsonValue> RawValue => base.RawValue as IDictionary<string, BsonValue>;

        /// <summary>
        /// Gets or sets the physical position of this document within the database.
        /// </summary>
        /// <remarks>
        /// This property is populated internally when documents are retrieved from the database using query operations.
        /// It represents the document's page address in the data file.
        /// </remarks>
        internal PageAddress RawId { get; set; } = PageAddress.Empty;

        /// <summary>
        /// Gets or sets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key of the element to get or set.</param>
        /// <returns>
        /// The <see cref="BsonValue"/> associated with the specified key, or <see cref="BsonValue.Null"/> if the key is not found.
        /// </returns>
        /// <remarks>
        /// When setting a value, if <paramref name="key"/> already exists, its value is replaced. 
        /// If the value is <see langword="null"/>, it will be stored as <see cref="BsonValue.Null"/>.
        /// </remarks>
        public override BsonValue this[string key]
        {
            get
            {
                return this.RawValue.GetOrDefault(key, BsonValue.Null);
            }
            set
            {
                this.RawValue[key] = value ?? BsonValue.Null;
            }
        }

        #region CompareTo

        /// <summary>
        /// Compares this <see cref="BsonDocument"/> to another <see cref="BsonValue"/>.
        /// </summary>
        /// <param name="other">The <see cref="BsonValue"/> to compare with this instance.</param>
        /// <returns>
        /// A signed integer that indicates the relative order: -1 if this instance is less than <paramref name="other"/>,
        /// 0 if they are equal, or 1 if this instance is greater than <paramref name="other"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// If <paramref name="other"/> is not a <see cref="BsonDocument"/>, the comparison is based on <see cref="BsonType"/> order.
        /// </para>
        /// <para>
        /// When comparing two documents, elements are compared sequentially by their keys in iteration order.
        /// If all compared elements are equal, the document with fewer elements is considered less than the other.
        /// </para>
        /// </remarks>
        public override int CompareTo(BsonValue other)
        {
            // if types are different, returns sort type order
            if (other.Type != BsonType.Document) return this.Type.CompareTo(other.Type);

            var thisKeys = this.Keys.ToArray();
            var thisLength = thisKeys.Length;

            var otherDoc = other.AsDocument;
            var otherKeys = otherDoc.Keys.ToArray();
            var otherLength = otherKeys.Length;

            var result = 0;
            var i = 0;
            var stop = Math.Min(thisLength, otherLength);

            for (; 0 == result && i < stop; i++)
                result = this[thisKeys[i]].CompareTo(otherDoc[thisKeys[i]]);

            // are different
            if (result != 0) return result;

            // test keys length to check which is bigger
            if (i == thisLength) return i == otherLength ? 0 : -1;

            return 1;
        }

        #endregion

        #region IDictionary

        /// <summary>
        /// Gets a collection containing the keys in the document.
        /// </summary>
        public ICollection<string> Keys => this.RawValue.Keys;

        /// <summary>
        /// Gets a collection containing the values in the document.
        /// </summary>
        public ICollection<BsonValue> Values => this.RawValue.Values;

        /// <summary>
        /// Gets the number of key-value pairs contained in the document.
        /// </summary>
        public int Count => this.RawValue.Count;

        /// <summary>
        /// Gets a value indicating whether the document is read-only. Always returns <see langword="false"/>.
        /// </summary>
        public bool IsReadOnly => false;

        /// <summary>
        /// Determines whether the document contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the document.</param>
        /// <returns><see langword="true"/> if the document contains an element with the specified key; otherwise, <see langword="false"/>.</returns>
        public bool ContainsKey(string key) => this.RawValue.ContainsKey(key);

        /// <summary>
        /// Gets all document elements as key-value pairs, with <c>_id</c> returned first if it exists.
        /// </summary>
        /// <returns>
        /// An enumerable collection of key-value pairs. The <c>_id</c> element (if present) is always yielded first,
        /// followed by all other elements in their natural order.
        /// </returns>
        /// <remarks>
        /// This method ensures that the <c>_id</c> field is always the first element when serializing or enumerating the document,
        /// which is important for database operations and BSON serialization.
        /// </remarks>
        public IEnumerable<KeyValuePair<string, BsonValue>> GetElements()
        {
            if(this.RawValue.TryGetValue("_id", out var id))
            {
                yield return new KeyValuePair<string, BsonValue>("_id", id);
            }

            foreach(var item in this.RawValue.Where(x => x.Key != "_id"))
            {
                yield return item;
            }
        }

        /// <summary>
        /// Adds an element with the specified key and value to the document.
        /// </summary>
        /// <param name="key">The key of the element to add (case-insensitive).</param>
        /// <param name="value">The value of the element to add. If <see langword="null"/>, stores <see cref="BsonValue.Null"/>.</param>
        /// <exception cref="ArgumentException">Thrown when an element with the same key already exists.</exception>
        public void Add(string key, BsonValue value) => this.RawValue.Add(key, value ?? BsonValue.Null);

        /// <summary>
        /// Removes the element with the specified key from the document.
        /// </summary>
        /// <param name="key">The key of the element to remove (case-insensitive).</param>
        /// <returns><see langword="true"/> if the element was successfully removed; otherwise, <see langword="false"/>.</returns>
        public bool Remove(string key) => this.RawValue.Remove(key);

        /// <summary>
        /// Removes all elements from the document.
        /// </summary>
        public void Clear() => this.RawValue.Clear();

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key of the value to get (case-insensitive).</param>
        /// <param name="value">
        /// When this method returns, contains the value associated with the specified key, if the key is found;
        /// otherwise, <see langword="null"/>. This parameter is passed uninitialized.
        /// </param>
        /// <returns><see langword="true"/> if the document contains an element with the specified key; otherwise, <see langword="false"/>.</returns>
        public bool TryGetValue(string key, out BsonValue value) => this.RawValue.TryGetValue(key, out value);

        /// <summary>
        /// Adds a key-value pair to the document.
        /// </summary>
        /// <param name="item">The key-value pair to add.</param>
        /// <exception cref="ArgumentException">Thrown when an element with the same key already exists.</exception>
        public void Add(KeyValuePair<string, BsonValue> item) => this.Add(item.Key, item.Value);

        /// <summary>
        /// Determines whether the document contains the specified key-value pair.
        /// </summary>
        /// <param name="item">The key-value pair to locate.</param>
        /// <returns><see langword="true"/> if the document contains the specified key-value pair; otherwise, <see langword="false"/>.</returns>
        public bool Contains(KeyValuePair<string, BsonValue> item) => this.RawValue.Contains(item);

        /// <summary>
        /// Removes the element with the key from the specified key-value pair.
        /// </summary>
        /// <param name="item">The key-value pair whose key should be removed.</param>
        /// <returns><see langword="true"/> if the element was successfully removed; otherwise, <see langword="false"/>.</returns>
        public bool Remove(KeyValuePair<string, BsonValue> item) => this.Remove(item.Key);

        /// <summary>
        /// Returns an enumerator that iterates through the document's key-value pairs.
        /// </summary>
        /// <returns>An enumerator for the document.</returns>
        public IEnumerator<KeyValuePair<string, BsonValue>> GetEnumerator() => this.RawValue.GetEnumerator();

        /// <summary>
        /// Returns an enumerator that iterates through the document's key-value pairs.
        /// </summary>
        /// <returns>An enumerator for the document.</returns>
        IEnumerator IEnumerable.GetEnumerator() => this.RawValue.GetEnumerator();

        /// <summary>
        /// Copies the elements of the document to an array, starting at the specified array index.
        /// </summary>
        /// <param name="array">The one-dimensional array that is the destination of the elements.</param>
        /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="array"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="arrayIndex"/> is less than 0.</exception>
        /// <exception cref="ArgumentException">Thrown when the number of elements exceeds the available space.</exception>
        public void CopyTo(KeyValuePair<string, BsonValue>[] array, int arrayIndex)
        {
            ((ICollection<KeyValuePair<string, BsonValue>>)this.RawValue).CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Copies all elements from this document to another <see cref="BsonDocument"/>.
        /// </summary>
        /// <param name="other">The target <see cref="BsonDocument"/> to copy elements into.</param>
        /// <remarks>
        /// If <paramref name="other"/> already contains keys that exist in this document, their values will be overwritten.
        /// </remarks>
        public void CopyTo(BsonDocument other)
        {
            foreach(var element in this)
            {
                other[element.Key] = element.Value;
            }
        }

        #endregion

        private int _length = 0;

        internal override int GetBytesCount(bool recalc)
        {
            if (recalc == false && _length > 0) return _length;

            var length = 5;

            foreach(var element in this.RawValue)
            {
                length += this.GetBytesCountElement(element.Key, element.Value);
            }

            return _length = length;
        }
    }
}
