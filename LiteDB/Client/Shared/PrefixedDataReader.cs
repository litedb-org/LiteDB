using System.Collections.Generic;

namespace LiteDB
{
    /// <summary>
    /// Continues a reader whose first values were already consumed into a buffer:
    /// yields the buffered values, then the reader's current value (the one that
    /// exceeded the buffer), then the rest of the reader. Like BsonDataReader, the
    /// first value is current before the first Read.
    /// </summary>
    internal sealed class PrefixedDataReader : IBsonDataReader
    {
        private readonly IReadOnlyList<BsonValue> _prefix;
        private readonly IBsonDataReader _reader;
        private int _index = -1;
        private bool _pendingTaken;
        private BsonValue _current;

        internal PrefixedDataReader(IReadOnlyList<BsonValue> prefix, IBsonDataReader reader)
        {
            _prefix = prefix;
            _reader = reader;
            _current = prefix.Count > 0 ? prefix[0] : reader.Current;
        }

        public string Collection => _reader.Collection;

        public bool HasValues => true;

        public BsonValue Current => _current;

        public BsonValue this[string field] => _current.AsDocument[field] ?? BsonValue.Null;

        public bool Read()
        {
            if (_index + 1 < _prefix.Count)
            {
                _current = _prefix[++_index];
                return true;
            }
            _index = _prefix.Count;
            if (!_pendingTaken)
            {
                // The reader is positioned on the value that did not fit the buffer.
                _pendingTaken = true;
                _current = _reader.Current;
                return true;
            }
            if (!_reader.Read()) return false;
            _current = _reader.Current;
            return true;
        }

        public void Dispose() => _reader.Dispose();
    }
}
