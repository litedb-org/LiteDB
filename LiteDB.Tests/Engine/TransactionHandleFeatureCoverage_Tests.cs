using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleFeatureCoverage_Tests
    {
        [Fact]
        public async Task Independent_direct_handles_overlap_and_preserve_each_others_commit_and_rollback()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file))
            using (var ready = new CountdownEvent(4))
            using (var release = new ManualResetEventSlim())
            {
                for (var i = 0; i < 4; i++) db.GetCollection("rows" + i).EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                var tasks = Enumerable.Range(0, 4).Select(id => Task.Factory.StartNew(() =>
                {
                    using var tx = db.BeginTransaction();
                    var rows = tx.GetCollection("rows" + id);
                    rows.Insert(new BsonDocument { ["_id"] = id, ["value"] = id * 10 });
                    Assert.NotNull(rows.FindById(id));
                    ready.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    if (id % 2 == 0) tx.Commit();
                    else tx.Rollback();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                try
                {
                    Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "All four handles must hold uncommitted writes concurrently.");
                    for (var i = 0; i < 4; i++) Assert.Empty(db.GetCollection("rows" + i).FindAll());
                }
                finally { release.Set(); }
                await Task.WhenAll(tasks);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(file);
                for (var i = 0; i < 4; i++)
                {
                    var expected = i % 2 == 0 ? new[] { i } : new int[0];
                    Assert.Equal(expected, cold.GetCollection("rows" + i).FindAll().Select(row => row["_id"].AsInt32));
                    Assert.Equal(expected, cold.GetCollection("rows" + i).Find(Query.EQ("value", i * 10)).Select(row => row["_id"].AsInt32));
                }
                Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Writable_handle_rejects_drop_and_rename_without_losing_prior_writes(bool shared)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString
            { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }))
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.ThrowsAny<NotSupportedException>(() => tx.DropCollection("rows"));
                Assert.ThrowsAny<NotSupportedException>(() => tx.RenameCollection("rows", "renamed"));
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.NotNull(tx.GetCollection("rows").FindById(1));
                tx.Commit();
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
            Assert.False(cold.CollectionExists("renamed"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Handle_metadata_queries_read_existing_collections_and_bound_uncommitted_indexes(bool shared)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file))
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            using (var db = new LiteDatabase(new ConnectionString
            { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }))
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").EnsureIndex("value");
                Assert.NotNull(tx.GetCollection("$cols").FindOne("name = 'rows'"));
                Assert.NotNull(tx.GetCollection("$indexes").FindOne("collection = 'rows' AND name = 'value'"));
                tx.Rollback();
            }
            using var cold = new LiteDatabase(file);
            Assert.True(cold.CollectionExists("rows"));
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
            Assert.Null(cold.GetCollection("$indexes").FindOne("collection = 'rows' AND name = 'value'"));
        }

        [Fact]
        public void Custom_engine_rejection_leaves_legacy_and_ordinary_operations_usable()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var custom = new TransparentEngine(engine))
            using (var db = new LiteDatabase(custom, disposeOnClose: false))
            {
                Assert.Throws<NotSupportedException>(() => db.BeginTransaction());
                Assert.True(db.BeginTrans());
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.True(db.Commit());
                Assert.NotNull(db.GetCollection("rows").FindById(1));
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
        }

        [Fact]
        public void Shared_legacy_owner_rejects_same_thread_handle_without_aborting_legacy_transaction()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
            {
                Assert.True(db.BeginTrans());
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
                Assert.True(db.Commit());
                using var next = db.BeginTransaction();
                Assert.NotNull(next.GetCollection("rows").FindById(1));
                next.Commit();
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
        }

        private sealed class TransparentEngine : EngineFacade
        {
            internal TransparentEngine(ILiteEngine engine) : base(engine) { }
            protected override T Invoke<T>(Func<T> action) => action();
            public override void Dispose() { }
        }
    }
}
