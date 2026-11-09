using System;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>The engine seen by a handle's collections: every call runs inside that handle.</summary>
    internal sealed class TransactionEngine : EngineFacade
    {
        private readonly LiteTransaction _owner;
        internal TransactionEngine(LiteTransaction owner) : base(null) { _owner = owner; }
        protected override ILiteEngine Inner => _owner.Storage;
        protected override T Invoke<T>(Func<T> action) => _owner.Dispatch(action);
        public override IBsonDataReader Query(string collection, Query query) => _owner.Query(collection, query);
        private static NotSupportedException Unsupported() => new NotSupportedException("Use the transaction handle for completion; nested transactions, maintenance and pragma changes are not transaction-bound operations.");
        public override bool BeginTrans() => throw Unsupported();
        public override bool Commit() => throw Unsupported();
        public override bool Rollback() => throw Unsupported();
        public override int Checkpoint() => throw Unsupported();
        public override long Rebuild(RebuildOptions options) => throw Unsupported();
        public override bool Pragma(string name, BsonValue value) => throw Unsupported();
        public override void Dispose() => throw Unsupported();
    }

    /// <summary>A reader owned by its handle; the handle closes it on completion.</summary>
    internal sealed class TransactionReader : IBsonDataReader
    {
        private readonly LiteTransaction _owner;
        private IBsonDataReader _inner;
        internal TransactionReader(LiteTransaction owner, IBsonDataReader inner) { _owner = owner; _inner = inner; }
        private IBsonDataReader Reader => _inner ?? throw new ObjectDisposedException(nameof(IBsonDataReader));
        public BsonValue Current => _owner.Dispatch(() => Reader.Current, authorizeEngine: false);
        public BsonValue this[string field] => _owner.Dispatch(() => Reader[field], authorizeEngine: false);
        public string Collection => _owner.Dispatch(() => Reader.Collection, authorizeEngine: false);
        public bool HasValues => _owner.Dispatch(() => Reader.HasValues, authorizeEngine: false);
        public bool Read() => _owner.Dispatch(() => Reader.Read(), authorizeEngine: false);
        public void Dispose() { if (_inner != null) _owner.ReleaseReader(this); }
        internal void Close()
        {
            var inner = _inner;
            _inner = null;
            inner?.Dispose();
        }
    }
}
