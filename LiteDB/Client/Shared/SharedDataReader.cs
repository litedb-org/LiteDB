using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;

namespace LiteDB
{
    public class SharedDataReader : IBsonDataReader
    {
        private readonly IBsonDataReader _reader;
        private readonly Action _dispose;

        private int _disposed;

        public SharedDataReader(IBsonDataReader reader, Action dispose)
        {
            _reader = reader;
            _dispose = dispose;
        }

        public BsonValue this[string field] => _reader[field];

        public string Collection => _reader.Collection;

        public BsonValue Current => _reader.Current;

        public bool HasValues => _reader.HasValues;

        public bool Read() => _reader.Read();

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SharedDataReader()
        {
            this.Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Atomic admission: the callback ends one mutex recursion and one engine user.
            // Two threads disposing at once must not both run it, or the second would end
            // another reader's ownership and could close the engine under it.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (disposing)
            {
                try { _reader.Dispose(); }
                finally { _dispose(); }
            }
        }
    }
}
