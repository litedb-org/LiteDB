using LiteDB.Engine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Class to read void, one or a collection of BsonValues. Used in SQL execution commands and query returns. Use local data source (IEnumerable[BsonDocument])
    /// </summary>
    public class BsonDataReader : IBsonDataReader
    {
        private readonly IEnumerator<BsonValue> _source = null;
        private readonly EngineState _state = null;
        private readonly string _collection = null;
        private readonly bool _hasValues;

        private BsonValue _current = null;
        private bool _isFirst;
        private bool _disposed = false;


        /// <summary>
        /// Initialize with no value
        /// </summary>
        internal BsonDataReader()
        {
            _hasValues = false;
        }

        /// <summary>
        /// Initialize with a single value
        /// </summary>
        internal BsonDataReader(BsonValue value, string collection = null)
        {
            _current = value;
            _isFirst = _hasValues = true;
            _collection = collection;
        }

        /// <summary>
        /// Initialize with an IEnumerable data source
        /// </summary>
        internal BsonDataReader(IEnumerable<BsonValue> values, string collection, EngineState state)
        {
            _collection = collection;
            _source = values.GetEnumerator();
            _state = state;

            try
            {
                _state.Validate();

                if (_source.MoveNext())
                {
                    _hasValues = _isFirst = true;
                    _current = _state.ReadTransform(_collection, _source.Current);
                }
            }
            catch (Exception ex)
            {
                _state.Handle(ex);
                throw;
            }
        }

        /// <summary>
        /// Return if has any value in result
        /// </summary>
        public bool HasValues => _hasValues;

        /// <summary>
        /// Return current value
        /// </summary>
        public BsonValue Current => _current;

        /// <summary>
        /// Return collection name
        /// </summary>
        public string Collection => _collection;

        /// <summary>
        /// Move cursor to next result. Returns true if read was possible
        /// </summary>
        public bool Read()
        {
            if (!_hasValues) return false;

            if (_isFirst)
            {
                _isFirst = false;
                return true;
            }
            else
            {
                if (_source != null)
                {
                    _state.Validate(); // checks if engine still open

                    try
                    {
                        var read = _source.MoveNext(); // can throw any error here
                        _current = _state.ReadTransform(_collection, _source.Current);
                        return read;
                    }
                    catch (Exception ex)
                    {
                        _state.Handle(ex);
                        throw ex;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        public BsonValue this[string field]
        {
            get
            {
                return _current.AsDocument[field] ?? BsonValue.Null;
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BsonDataReader()
        {
            this.Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            _disposed = true;

            if (disposing)
            {
                _source?.Dispose();
            }
        }
    }
}