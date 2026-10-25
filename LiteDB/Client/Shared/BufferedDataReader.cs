using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// A completed result held in memory. It owns no engine, lock or lease.
    /// A failure met while producing it is raised after the rows that preceded it.
    /// </summary>
    internal sealed class BufferedDataReader : IBsonDataReader
    {
        private readonly IReadOnlyList<BsonValue> _values;
        private readonly ExceptionDispatchInfo _failure;
        private int _index = -1;

        internal BufferedDataReader(IReadOnlyList<BsonValue> values, string collection, ExceptionDispatchInfo failure = null)
        {
            _values = values;
            _failure = failure;
            this.Collection = collection;
        }

        public string Collection { get; }

        public bool HasValues => _values.Count > 0;

        // Like BsonDataReader, the first value is current before the first Read.
        public BsonValue Current => _values.Count == 0 ? null : _values[System.Math.Max(_index, 0)];

        public BsonValue this[string field] => this.Current.AsDocument[field] ?? BsonValue.Null;

        public bool Read()
        {
            if (_index + 1 >= _values.Count)
            {
                _failure?.Throw();
                return false;
            }
            _index++;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
