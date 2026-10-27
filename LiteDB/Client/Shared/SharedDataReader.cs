using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public class SharedDataReader : IBsonDataReader
    {
        private readonly IBsonDataReader _reader;
        private readonly Action _dispose;
        private readonly Func<bool> _read;
        // Set when the reader streams under its connection's native ownership.
        private readonly string _namespace;
        private readonly object _connection;
        private readonly Func<bool> _retains;
        private readonly SharedReaderCleanup _cleanup;

        private int _disposed;

        public SharedDataReader(IBsonDataReader reader, Action dispose)
        {
            _reader = reader;
            _dispose = dispose;
        }

        internal SharedDataReader(IBsonDataReader reader, Action dispose, bool ownsResources)
            : this(reader, dispose)
        {
            if (ownsResources) _cleanup = new SharedReaderCleanup(reader, dispose);
        }

        /// <summary>
        /// A reader that keeps its connection's native ownership until disposed. Each read, and
        /// the disposal, runs in a frame of that connection, so a callback it invokes cannot wait
        /// for the ownership through another connection.
        /// </summary>
        internal SharedDataReader(IBsonDataReader reader, Action dispose, string ns, object connection, Func<bool> retains, Func<bool> read = null)
            : this(reader, dispose)
        {
            _read = read;
            _namespace = ns;
            _connection = connection;
            _retains = retains;
            _cleanup = new SharedReaderCleanup(reader, dispose, ns, connection, retains);
        }

        public BsonValue this[string field] => _reader[field];

        public string Collection => _reader.Collection;

        public BsonValue Current => _reader.Current;

        public bool HasValues => _reader.HasValues;

        public bool Read()
        {
            if (_retains == null) return _reader.Read();
            using (SharedCallFrames.Enter(_namespace, _connection, _retains)) return _read != null ? _read() : _reader.Read();
        }

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
                if (_retains == null) this.Close();
                // Ending the ownership can close its engine, which writes through caller streams.
                else using (SharedCallFrames.Enter(_namespace, _connection, _retains)) this.Close();
            }
            else _cleanup?.Abandon();
        }

        private void Close()
        {
            if (_cleanup != null) { _cleanup.Dispose(); return; }
            try { _reader.Dispose(); }
            finally { _dispose(); }
        }
    }
}
