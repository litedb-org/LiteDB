using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>An engine decorator that forwards every call; deliberately broken engines override some.</summary>
    public class ForwardingEngine : ILiteEngine
    {
        protected ForwardingEngine(ILiteEngine inner)
        {
            this.Inner = inner;
        }

        protected ILiteEngine Inner { get; }

        public virtual int Checkpoint() => this.Inner.Checkpoint();
        public virtual long Rebuild(RebuildOptions options) => this.Inner.Rebuild(options);
        public virtual bool BeginTrans() => this.Inner.BeginTrans();
        public virtual bool Commit() => this.Inner.Commit();
        public virtual bool Rollback() => this.Inner.Rollback();
        public virtual IBsonDataReader Query(string collection, Query query) => this.Inner.Query(collection, query);
        public virtual int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => this.Inner.Insert(collection, docs, autoId);
        public virtual int Update(string collection, IEnumerable<BsonDocument> docs) => this.Inner.Update(collection, docs);
        public virtual int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => this.Inner.UpdateMany(collection, transform, predicate);
        public virtual int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => this.Inner.Upsert(collection, docs, autoId);
        public virtual int Delete(string collection, IEnumerable<BsonValue> ids) => this.Inner.Delete(collection, ids);
        public virtual int DeleteMany(string collection, BsonExpression predicate) => this.Inner.DeleteMany(collection, predicate);
        public virtual bool DropCollection(string name) => this.Inner.DropCollection(name);
        public virtual bool RenameCollection(string name, string newName) => this.Inner.RenameCollection(name, newName);
        public virtual bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => this.Inner.EnsureIndex(collection, name, expression, unique);
        public virtual bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options) => this.Inner.EnsureVectorIndex(collection, name, expression, options);
        public virtual bool DropIndex(string collection, string name) => this.Inner.DropIndex(collection, name);
        public virtual BsonValue Pragma(string name) => this.Inner.Pragma(name);
        public virtual bool Pragma(string name, BsonValue value) => this.Inner.Pragma(name, value);
        public void Dispose() => this.Inner.Dispose();
    }

    /// <summary>
    /// Broken on purpose: every second successful Commit discards the transaction's writes but still
    /// reports success (a lost committed write). Visible to a single thread.
    /// </summary>
    public sealed class LosesCommittedWritesEngine : ForwardingEngine
    {
        private int _commits;

        public LosesCommittedWritesEngine(ILiteEngine inner) : base(inner)
        {
        }

        public override bool Commit()
        {
            if (Interlocked.Increment(ref _commits) % 2 == 0 && this.Inner.Rollback()) return true;
            return this.Inner.Commit();
        }
    }

    /// <summary>
    /// Broken on purpose: Update is a delete followed by a re-insert in separate calls, so another
    /// thread can observe the document missing in between. Every single-thread result is still right;
    /// only concurrent readers can see the intermediate state. While another thread has a call in
    /// flight, the half-done state stays exposed until one of its reads completes (at most 250 ms),
    /// so detection does not depend on a reader happening to land in a short window.
    /// </summary>
    public sealed class NonAtomicUpdateEngine : ForwardingEngine
    {
        private int _inFlight;
        private int _readsCompleted;

        public NonAtomicUpdateEngine(ILiteEngine inner) : base(inner)
        {
        }

        public override int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            var updated = 0;
            foreach (var doc in docs.ToList())
            {
                if (this.Inner.Delete(collection, new[] { doc["_id"] }) == 0) continue;
                var reads = Volatile.Read(ref _readsCompleted);
                var exposed = System.Diagnostics.Stopwatch.StartNew();
                SpinWait.SpinUntil(() => Volatile.Read(ref _readsCompleted) != reads ||
                    (Volatile.Read(ref _inFlight) == 0 && exposed.ElapsedMilliseconds >= 20), 250);
                this.Inner.Insert(collection, new[] { doc }, BsonAutoId.Int32);
                updated++;
            }
            return updated;
        }

        // The updater's own Delete/Insert go to Inner directly, so these count other threads only.
        public override IBsonDataReader Query(string collection, Query query)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                return new CountedReader(this.Inner.Query(collection, query), this);
            }
            catch
            {
                Interlocked.Decrement(ref _inFlight);
                throw;
            }
        }

        public override int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => this.Counted(() => this.Inner.Insert(collection, docs, autoId));

        public override int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => this.Counted(() => this.Inner.Upsert(collection, docs, autoId));

        public override int Delete(string collection, IEnumerable<BsonValue> ids) => this.Counted(() => this.Inner.Delete(collection, ids));

        private T Counted<T>(Func<T> call)
        {
            Interlocked.Increment(ref _inFlight);
            try { return call(); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        private sealed class CountedReader : IBsonDataReader
        {
            private readonly IBsonDataReader _inner;
            private readonly NonAtomicUpdateEngine _owner;
            private int _disposed;

            public CountedReader(IBsonDataReader inner, NonAtomicUpdateEngine owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public BsonValue this[string field] => _inner[field];
            public string Collection => _inner.Collection;
            public BsonValue Current => _inner.Current;
            public bool HasValues => _inner.HasValues;
            public bool Read() => _inner.Read();

            public void Dispose()
            {
                try { _inner.Dispose(); }
                finally
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    {
                        Interlocked.Increment(ref _owner._readsCompleted);
                        Interlocked.Decrement(ref _owner._inFlight);
                    }
                }
            }
        }
    }
}
