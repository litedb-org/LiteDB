using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandle_Tests
    {
        private static ConnectionString Settings(string file, bool shared = false, bool encrypted = false) =>
            new ConnectionString { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct,
                Password = encrypted ? "secret" : null, TransactionPageLimit = 1 };
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        internal static void OnThread(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Worker did not complete.");
            if (failure != null) throw new Exception("Worker failed", failure);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Creator_retirement_and_sequential_handoff_preserve_commit_and_rollback(bool shared, bool encrypted)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(Settings(file, shared, encrypted)))
            {
                db.GetCollection("rows").EnsureIndex("value", true);
                db.GetCollection("sentinel").Insert(Row(9));
                ILiteTransaction tx = null;
                OnThread(() => { tx = db.BeginTransaction(); tx.GetCollection("rows").Insert(Row(1)); });
                using (tx)
                {
                    OnThread(() => { Assert.NotNull(tx.GetCollection("rows").FindById(1)); tx.GetCollection("rows").Insert(Row(2)); });
                    OnThread(tx.Commit);
                    Assert.Equal(LiteTransactionState.Committed, tx.State);
                    Assert.Throws<InvalidOperationException>(tx.Commit);
                }
                using var aborted = db.BeginTransaction();
                OnThread(() => aborted.GetCollection("rows").Insert(Row(3)));
                OnThread(aborted.Rollback);
                Assert.Equal(LiteTransactionState.RolledBack, aborted.State);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(Settings(file, shared, encrypted));
                Assert.Equal(new[] { 1, 2 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
                Assert.Equal(2, db.GetCollection("rows").Find(Query.GTE("value", 10)).Count());
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }

        [Fact]
        public void Handles_and_ordinary_calls_do_not_enlist_in_each_other()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            using var first = db.BeginTransaction();
            using var second = db.BeginTransaction();
            first.GetCollection("first").Insert(Row(1));
            second.GetCollection("second").Insert(Row(2));
            db.GetCollection("ordinary").Insert(Row(3));
            Assert.False(db.Commit());
            Assert.False(db.Rollback());
            first.Rollback();
            second.Commit();
            Assert.Equal(0, db.GetCollection("first").Count());
            Assert.Equal(1, db.GetCollection("second").Count());
            Assert.Equal(1, db.GetCollection("ordinary").Count());
        }

        [Fact]
        public void Unbound_calls_inside_bound_input_do_not_join_and_statement_failure_is_terminal()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            using var tx = db.BeginTransaction();
            IEnumerable<BsonDocument> Input()
            {
                yield return Row(1);
                db.GetCollection("ordinary").Insert(Row(2));
                throw new InvalidOperationException("input failed");
            }
            var rows = tx.GetCollection("rows");
            Assert.Throws<InvalidOperationException>(() => rows.Insert(Input()));
            Assert.Equal(LiteTransactionState.Failed, tx.State);
            Assert.Throws<InvalidOperationException>(() => rows.Insert(Row(3)));
            Assert.Equal(0, db.GetCollection("rows").Count());
            Assert.Equal(1, db.GetCollection("ordinary").Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cursors_block_commit_and_bound_queries_never_escape_after_completion(bool shared)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(Settings(file, shared));
            using var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            rows.Insert(new[] { Row(1), Row(2) });
            var deferred = rows.FindAll();
            using (var reader = rows.Query().ExecuteReader())
            {
                OnThread(() => Assert.True(reader.Read()));
                Assert.Throws<InvalidOperationException>(tx.Commit);
                Assert.Equal(LiteTransactionState.Active, tx.State);
                OnThread(() => Assert.True(reader.Read()));
            }
            tx.Commit();
            Assert.Throws<InvalidOperationException>(() => deferred.ToArray());
            Assert.Throws<InvalidOperationException>(() => rows.Insert(Row(3)));
        }

        [Fact]
        public async Task Overlap_is_rejected_without_aborting_active_work_and_close_drains_it()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            using var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IEnumerable<BsonDocument> Input()
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                yield return Row(1);
            }
            var write = Task.Run(() => rows.Insert(Input()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidOperationException>(tx.Commit);
            Assert.Throws<InvalidOperationException>(tx.Dispose);
            Assert.Throws<InvalidOperationException>(() => rows.Count());
            Assert.Equal(LiteTransactionState.Active, tx.State);
            var close = Task.Run(db.Dispose);
            Assert.False(close.Wait(100));
            release.Set();
            await write;
            await close;
            Assert.Equal(LiteTransactionState.RolledBack, tx.State);
            // Direct opens the file exclusively: check through a fresh open after close.
            using var reopened = new LiteDatabase(file);
            Assert.Equal(0, reopened.GetCollection("rows").Count());
            reopened.GetCollection("rows").Insert(Row(2));
            Assert.Equal(1, reopened.GetCollection("rows").Count());
        }
    }
}
