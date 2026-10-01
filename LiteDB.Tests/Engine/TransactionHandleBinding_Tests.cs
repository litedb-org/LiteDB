using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleBinding_Tests
    {
        public sealed class Entity
        {
            public int Id { get; set; }
            public int Value { get; set; }
            public float[] Vector { get; set; }
        }

        public sealed class CallbackEntity
        {
            [BsonIgnore] public Action Reading;
            public int Id { get; set; }
            public int Value { get { Reading?.Invoke(); return 42; } set { } }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Mapper_reentry_cannot_complete_or_escape_the_enclosing_operation(bool shared)
        {
            using var file = new TempFile();
            using (var db = Open(file, shared))
            {
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection<CallbackEntity>("rows");
                rows.Insert(new CallbackEntity { Id = 1 });
                rows.Insert(new CallbackEntity { Id = 2, Reading = () =>
                {
                    Assert.Throws<InvalidOperationException>(tx.Commit);
                    Assert.Throws<InvalidOperationException>(tx.Rollback);
                    Assert.Throws<InvalidOperationException>(tx.Dispose);
                    Assert.Throws<InvalidOperationException>(db.Dispose);
                    Assert.Throws<InvalidOperationException>(() => rows.Count());
                }});
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.Equal(2, rows.Count());
                tx.Rollback();
            }
            using var reopened = Open(file, shared);
            Assert.Equal(0, reopened.GetCollection("rows").Count());
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
        }

        [Fact]
        public async Task Mapping_is_inside_the_same_handle_overlap_guard()
        {
            using var file = new TempFile();
            using var db = Open(file, false);
            using var tx = db.BeginTransaction();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var rows = tx.GetCollection<CallbackEntity>("rows");
            var write = Task.Run(() => rows.Insert(new CallbackEntity { Id = 1, Reading = () =>
            {
                entered.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(20)));
            }}));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.Throws<InvalidOperationException>(tx.Commit);
                Assert.Throws<InvalidOperationException>(() => rows.Insert(new CallbackEntity { Id = 2 }));
                Assert.Equal(LiteTransactionState.Active, tx.State);
            }
            finally { resume.Set(); }
            await write;
            tx.Commit();
            Assert.Equal(1, db.GetCollection("rows").Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Bound_indexes_queries_and_deferred_objects_preserve_their_owner(bool shared)
        {
            using var file = new TempFile();
            using (var db = Open(file, shared))
            {
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection<Entity>("rows");
                rows.Insert(new Entity { Id = 1, Value = 10, Vector = new[] { 1f, 0f } });
                rows.Insert(new Entity { Id = 2, Value = 20, Vector = new[] { 0f, 1f } });
                Assert.True(rows.EnsureIndex(x => x.Value));
                Assert.False(rows.EnsureIndex(x => x.Value));
                Assert.Equal(2, rows.Query().OrderBy(x => x.Value).Select(x => x.Value).ToArray().Length);
                Assert.Equal(1, rows.Query().WhereNear(x => x.Vector, new[] { 1f, 0f }, 0.1).Count());
                Assert.True(tx.CollectionExists("rows"));
                var deferred = rows.Query().ToEnumerable();
                using var reader = rows.Query().ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(reader.Current["Value"], reader["Value"]);
                Assert.Throws<InvalidOperationException>(tx.Commit);
                reader.Dispose();
                tx.Rollback();
                Assert.Throws<InvalidOperationException>(() => deferred.ToArray());
                Assert.Throws<InvalidOperationException>(() => rows.Query());
                Assert.Throws<ObjectDisposedException>(() => reader.Read());
            }
            using var reopened = Open(file, shared);
            Assert.Equal(0, reopened.GetCollection<Entity>("rows").Count());
        }

        [Fact]
        public void Unsupported_external_query_output_does_not_abort_prior_writes()
        {
            using var file = new TempFile();
            using var db = Open(file, false);
            using var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            var query = Query.All();
            var deferred = rows.Find(query);
            query.Into = "$file";
            Assert.ThrowsAny<NotSupportedException>(() => deferred.ToArray());
            Assert.Equal(LiteTransactionState.Active, tx.State);
            tx.Commit();
            Assert.Equal(1, db.GetCollection("rows").Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_only_handles_query_and_complete_without_granting_writes(bool shared)
        {
            using var file = new TempFile();
            using (var writer = Open(file, shared)) writer.GetCollection<Entity>("rows").Insert(new Entity { Id = 1, Value = 10 });
            var before = System.IO.File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, ReadOnly = true,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }))
            {
                Assert.ThrowsAny<Exception>(() => db.BeginTrans());
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection<Entity>("rows");
                Assert.Equal(10, rows.FindById(1).Value);
                Assert.ThrowsAny<Exception>(() => rows.Insert(new Entity { Id = 2 }));
                Assert.ThrowsAny<Exception>(() => tx.DropCollection("rows"));
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Commit();
                Assert.Equal(LiteTransactionState.Committed, tx.State);
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(file.Filename));
        }

        private static LiteDatabase Open(string file, bool shared) => new LiteDatabase(new ConnectionString
        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct });
    }
}
