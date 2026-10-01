using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleFailure_Tests
    {
        private static LiteEngine Core(ILiteTransaction transaction) => ((TransactionResources)typeof(LiteTransaction)
            .GetField("_resources", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(transaction)).Engine;

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Failed_wal_publication_never_reports_commit_or_disposal_as_rollback(bool shared, bool encrypted)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Password = encrypted ? "secret" : null,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var reached = false;
                Core(tx).SimulateDiskWriteFail = page => { reached = true; throw new IOException("injected WAL write failure"); };
                Assert.Throws<IOException>(tx.Commit);
                Assert.True(reached);
                Assert.Equal(LiteTransactionState.Indeterminate, tx.State);
                tx.Dispose();
                tx.Dispose();
                Assert.Equal(LiteTransactionState.Indeterminate, tx.State);
            }
            Verify(settings, new[] { 1 });
            Verify(settings, new[] { 1 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Committed_outcome_survives_post_commit_cleanup_failure(bool shared)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var monitor = Core(tx).GetMonitor();
                var reached = false;
                monitor.AfterTransactionExit = () =>
                {
                    monitor.AfterTransactionExit = null;
                    reached = true;
                    throw new IOException("injected committed cleanup failure");
                };
                Assert.Throws<IOException>(tx.Commit);
                Assert.True(reached);
                Assert.Equal(LiteTransactionState.Committed, tx.State);
                tx.Dispose();
                Assert.Equal(LiteTransactionState.Committed, tx.State);
            }
            Verify(settings, new[] { 1, 2 });
            Verify(settings, new[] { 1, 2 });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Failed_durable_flush_is_indeterminate_and_recovery_keeps_acknowledged_data(string password)
        {
            using var file = new TempFile();
            using var log = new FailingFlushFile(FileHelper.GetLogFile(file));
            using var data = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, DurableCommits = true };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                log.Fail = true;
                Assert.Throws<IOException>(tx.Commit);
                Assert.True(log.Reached);
                Assert.Equal(LiteTransactionState.Indeterminate, tx.State);
                tx.Dispose();
                Assert.Equal(LiteTransactionState.Indeterminate, tx.State);
            }
            log.Fail = false;
            // The injected flush failed after handing complete bytes to the OS: recovery may
            // find the write. This test intentionally does not interpret failure as rollback.
            for (var i = 0; i < 2; i++)
            {
                using var recovered = new LiteEngine(settings);
                using var db = new LiteDatabase(recovered, disposeOnClose: false);
                Assert.NotNull(db.GetCollection("rows").FindById(1));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
                var ids = db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                Assert.True(ids.SequenceEqual(new[] { 1 }) || ids.SequenceEqual(new[] { 1, 2 }));
                Assert.Equal(ids.Length, db.GetCollection("rows").Find(Query.GTE("value", 10)).Count());
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Statement_error_survives_rollback_cleanup_failure(bool shared)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            var original = new InvalidOperationException("input failed");
            var cleanup = new IOException("rollback cleanup failed");
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                var monitor = Core(tx).GetMonitor();
                monitor.AfterTransactionExit = () => { monitor.AfterTransactionExit = null; throw cleanup; };
                IEnumerable<BsonDocument> Input() { yield return Row(2); throw original; }
                Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tx.GetCollection("rows").Insert(Input())));
                Assert.Same(cleanup, original.Data["LiteDB.StatementRollback"]);
                Assert.Equal(LiteTransactionState.Failed, tx.State);
            }
            Verify(settings, new[] { 1 });
            Verify(settings, new[] { 1 });
        }

        private static void Seed(LiteDatabase db)
        {
            db.GetCollection("rows").Insert(Row(1));
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
            db.Checkpoint();
        }
        private static void Verify(ConnectionString settings, int[] ids)
        {
            using var db = new LiteDatabase(settings);
            Assert.Equal(ids, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            Assert.Equal(ids.Length, db.GetCollection("rows").Find(Query.GTE("value", 10)).Count());
            Assert.NotNull(db.GetCollection("sentinel").FindById(9));
        }
        private sealed class FailingFlushFile : FileStream
        {
            internal bool Fail, Reached;
            internal FailingFlushFile(string file) : base(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) { }
            public override void Flush(bool flushToDisk)
            {
                base.Flush(false);
                if (flushToDisk && Fail) { Reached = true; throw new IOException("injected durable flush failure"); }
                if (flushToDisk) base.Flush(true);
            }
        }
    }
}
