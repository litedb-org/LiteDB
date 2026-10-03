using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Utils;

namespace LiteDB
{
    public class SharedDataReader : IBsonDataReader
    {
        private readonly IBsonDataReader _reader;
        private readonly Action _dispose;
        // Set when the reader streams under its connection's native ownership.
        private readonly string _namespace;
        private readonly object _connection;
        private readonly Func<bool> _retains;

        private int _disposed;

        public SharedDataReader(IBsonDataReader reader, Action dispose)
        {
            _reader = reader;
            _dispose = dispose;
        }

        /// <summary>
        /// A reader that keeps its connection's native ownership until disposed. Each read, and
        /// the disposal, runs in a frame of that connection, so a callback it invokes cannot wait
        /// for the ownership through another connection.
        /// </summary>
        internal SharedDataReader(IBsonDataReader reader, Action dispose, string ns, object connection, Func<bool> retains)
            : this(reader, dispose)
        {
            _namespace = ns;
            _connection = connection;
            _retains = retains;
        }

        public BsonValue this[string field] => _reader[field];

        public string Collection => _reader.Collection;

        public BsonValue Current => _reader.Current;

        public bool HasValues => _reader.HasValues;

#if DEBUG || TESTING
        /// <summary>
        /// Wait-for graph: the owner whose hold this reader retains (its connection, or the pin it
        /// streams under). Its reads and disposal execute that owner's work on the calling thread.
        /// </summary>
        internal object GraphOwner { get; set; }
#endif

        public bool Read()
        {
#if DEBUG || TESTING
            LiteDB.Utils.WaitGraph.Enter(this.GraphOwner);
            try
            {
#endif
            if (_retains == null) return _reader.Read();
            using (SharedCallFrames.Enter(_namespace, _connection, _retains)) return _reader.Read();
#if DEBUG || TESTING
            }
            finally { LiteDB.Utils.WaitGraph.Exit(this.GraphOwner); }
#endif
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

        [TeardownPath("SharedDataReader.Dispose", TeardownDisposition.Propagated | TeardownDisposition.Discarded,
            "The inner reader's and the release callback's failures propagate (try/finally runs the callback either way); " +
            "the core closes the release triggers (pin end, last-reader checkpoint) drop their failure lists.")]
        protected virtual void Dispose(bool disposing)
        {
            // Atomic admission: the callback ends one mutex recursion and one engine user.
            // Two threads disposing at once must not both run it, or the second would end
            // another reader's ownership and could close the engine under it.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (disposing)
            {
#if DEBUG || TESTING
                LiteDB.Utils.WaitGraph.Enter(this.GraphOwner);
                try
                {
#endif
                if (_retains == null) this.Close();
                // Ending the ownership can close its engine, which writes through caller streams.
                else using (SharedCallFrames.Enter(_namespace, _connection, _retains)) this.Close();
#if DEBUG || TESTING
                }
                finally { LiteDB.Utils.WaitGraph.Exit(this.GraphOwner); }
#endif
            }
        }

        private void Close()
        {
            try { _reader.Dispose(); }
            finally { _dispose(); }
        }
    }
}
