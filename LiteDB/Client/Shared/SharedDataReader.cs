using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>
    /// Wraps an <see cref="IBsonDataReader"/> for use in shared engine mode, ensuring proper resource cleanup when disposed.
    /// </summary>
    /// <remarks>
    /// This reader delegates all operations to the underlying reader while providing custom disposal logic
    /// to release shared engine resources (such as read locks) when the reader is no longer needed.
    /// </remarks>
    public class SharedDataReader : IBsonDataReader
    {
        private readonly IBsonDataReader _reader;
        private readonly Action _dispose;

        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedDataReader"/> class.
        /// </summary>
        /// <param name="reader">The underlying <see cref="IBsonDataReader"/> to wrap.</param>
        /// <param name="dispose">The action to execute on disposal for resource cleanup.</param>
        public SharedDataReader(IBsonDataReader reader, Action dispose)
        {
            _reader = reader;
            _dispose = dispose;
        }

        /// <inheritdoc/>
        public BsonValue this[string field] => _reader[field];

        /// <inheritdoc/>
        public string Collection => _reader.Collection;

        /// <inheritdoc/>
        public BsonValue Current => _reader.Current;

        /// <inheritdoc/>
        public bool HasValues => _reader.HasValues;

        /// <inheritdoc/>
        public bool Read() => _reader.Read();

        /// <summary>
        /// Releases all resources used by the <see cref="SharedDataReader"/>.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer for <see cref="SharedDataReader"/>.
        /// </summary>
        ~SharedDataReader()
        {
            this.Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            _disposed = true;

            if (disposing)
            {
                _reader.Dispose();
                _dispose();
            }
        }
    }
}