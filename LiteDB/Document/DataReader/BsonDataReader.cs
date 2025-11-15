using LiteDB.Engine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Provides a data reader implementation for reading BSON values from local data sources.
    /// </summary>
    /// <remarks>
    /// <see cref="BsonDataReader"/> is used internally for SQL execution commands and query results.
    /// It supports reading void (no results), a single value, or a collection of values from an <see cref="IEnumerable{BsonValue}"/> data source.
    /// </remarks>
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
        /// Initializes a new instance of the <see cref="BsonDataReader"/> class with no values (empty result set).
        /// </summary>
        internal BsonDataReader()
        {
            _hasValues = false;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonDataReader"/> class with a single value.
        /// </summary>
        /// <param name="value">The single <see cref="BsonValue"/> to read.</param>
        /// <param name="collection">The collection name from which the value originated. Default is <see langword="null"/>.</param>
        internal BsonDataReader(BsonValue value, string collection = null)
        {
            _current = value;
            _isFirst = _hasValues = true;
            _collection = collection;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonDataReader"/> class with an enumerable collection of values.
        /// </summary>
        /// <param name="values">The enumerable collection of <see cref="BsonValue"/> to read.</param>
        /// <param name="collection">The collection name from which the values originated.</param>
        /// <param name="state">The engine state for validation and read transformation.</param>
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

        /// <inheritdoc/>
        public bool HasValues => _hasValues;

        /// <inheritdoc/>
        public BsonValue Current => _current;

        /// <inheritdoc/>
        public string Collection => _collection;

        /// <inheritdoc/>
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
                        // TODO: re-throw using only the "throw;" pattern to preserve stack trace
                        throw ex;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        /// <inheritdoc/>
        public BsonValue this[string field]
        {
            get
            {
                return _current.AsDocument[field] ?? BsonValue.Null;
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="BsonDataReader"/>.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer for <see cref="BsonDataReader"/>.
        /// </summary>
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