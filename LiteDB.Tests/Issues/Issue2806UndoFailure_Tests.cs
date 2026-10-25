using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2806UndoFailure_Tests
    {
        private sealed class FaultingEngine : ILiteEngine
        {
            private readonly ILiteEngine _inner;
            public Exception RollbackFault { get; set; }
            public Exception DeleteManyFault { get; set; }
            public FaultingEngine(ILiteEngine inner) => _inner = inner;
            public int Checkpoint() => _inner.Checkpoint();
            public long Rebuild(RebuildOptions options) => _inner.Rebuild(options);
            public bool BeginTrans() => _inner.BeginTrans();
            public bool Commit() => _inner.Commit();
            public bool Rollback()
            {
                var rolledBack = _inner.Rollback();
                if (RollbackFault != null) throw RollbackFault;
                return rolledBack;
            }
            public IBsonDataReader Query(string collection, Query query) => _inner.Query(collection, query);
            public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => _inner.Insert(collection, docs, autoId);
            public int Update(string collection, IEnumerable<BsonDocument> docs) => _inner.Update(collection, docs);
            public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => _inner.UpdateMany(collection, transform, predicate);
            public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => _inner.Upsert(collection, docs, autoId);
            public int Delete(string collection, IEnumerable<BsonValue> ids) => _inner.Delete(collection, ids);
            public int DeleteMany(string collection, BsonExpression predicate)
            {
                if (DeleteManyFault != null) throw DeleteManyFault;
                return _inner.DeleteMany(collection, predicate);
            }
            public bool DropCollection(string name) => _inner.DropCollection(name);
            public bool RenameCollection(string name, string newName) => _inner.RenameCollection(name, newName);
            public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => _inner.EnsureIndex(collection, name, expression, unique);
            public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options) => _inner.EnsureVectorIndex(collection, name, expression, options);
            public bool DropIndex(string collection, string name) => _inner.DropIndex(collection, name);
            public BsonValue Pragma(string name) => _inner.Pragma(name);
            public bool Pragma(string name, BsonValue value) => _inner.Pragma(name, value);
            public void Dispose() => _inner.Dispose();
        }

        private sealed class InterruptedSource : MemoryStream
        {
            private readonly Action _beforeFailure;
            public InterruptedSource(Action beforeFailure = null) : base(new byte[600000]) => _beforeFailure = beforeFailure;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position < 300000) return base.Read(buffer, offset, (int)Math.Min(count, 300000 - Position));
                _beforeFailure?.Invoke();
                throw new IOException("source interrupted");
            }
        }

        [Fact]
        public void Rollback_failure_of_an_upload_owned_transaction_is_reported_with_the_upload_error()
        {
            var engine = new FaultingEngine(new LiteEngine(new EngineSettings { Filename = ":memory:" }));
            using var db = new LiteDatabase(engine);
            engine.RollbackFault = new InvalidOperationException("rollback broke");

            Action upload = () => db.FileStorage.Upload("asset", "new.bin", new InterruptedSource());

            var error = upload.Should().Throw<LiteException>().WithMessage("*could not be rolled back*rollback broke*").Which;
            var causes = error.InnerException.Should().BeOfType<AggregateException>().Which.InnerExceptions;
            causes[0].Should().BeOfType<IOException>().Which.Message.Should().Be("source interrupted");
            causes[1].Should().BeSameAs(engine.RollbackFault);
        }

        [Fact]
        public void Failure_to_undo_an_upload_inside_a_caller_transaction_says_the_transaction_is_gone()
        {
            var engine = new FaultingEngine(new LiteEngine(new EngineSettings { Filename = ":memory:" }));
            using var db = new LiteDatabase(engine);
            db.BeginTrans().Should().BeTrue();
            var undoFault = new InvalidOperationException("undo broke");

            Action upload = () => db.FileStorage.Upload("asset", "new.bin", new InterruptedSource(() => engine.DeleteManyFault = undoFault));

            var error = upload.Should().Throw<LiteException>().WithMessage("*surrounding transaction*").Which;
            var causes = error.InnerException.Should().BeOfType<AggregateException>().Which.InnerExceptions;
            causes[0].Should().BeOfType<IOException>();
            causes[1].Should().BeSameAs(undoFault);
        }
    }
}
