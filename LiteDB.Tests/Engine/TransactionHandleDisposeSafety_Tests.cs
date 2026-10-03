using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleDisposeSafety_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Disposed_bound_objects_refuse_use_without_aborting_prior_writes(bool shared)
        {
            using var file = new TempFile();
            var settings = Settings(file, shared);
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection("rows");
                rows.Insert(Row(2));
                var reader = tx.GetCollection("rows").Query().ExecuteReader();
                Assert.True(reader.Read());
                reader.Dispose();
                Assert.Throws<ObjectDisposedException>(() => reader.Read());
                Assert.Throws<ObjectDisposedException>(() => reader.Current);
                Assert.Throws<ObjectDisposedException>(() => reader["value"]);
                Assert.Throws<ObjectDisposedException>(() => reader.Collection);
                Assert.Throws<ObjectDisposedException>(() => reader.HasValues);
                var iterator = rows.FindAll().GetEnumerator();
                Assert.True(iterator.MoveNext());
                iterator.Dispose();
                Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext());
                Assert.Throws<ObjectDisposedException>(() => iterator.Current);
                reader.Dispose();
                iterator.Dispose();
                Assert.Equal(LiteTransactionState.Active, tx.State);
                rows.Insert(Row(3));
                tx.Commit();
            }
            Verify(settings, new[] { 1, 2, 3 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Disposal_while_close_worker_rolls_back_is_idempotent(bool shared)
        {
            using var file = new TempFile();
            var settings = Settings(file, shared);
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var reader = tx.GetCollection("rows").Query().ExecuteReader();
                var iterator = tx.GetCollection("rows").FindAll().GetEnumerator();
                Assert.True(iterator.MoveNext());
                using var reached = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                var monitor = Core(tx).GetMonitor();
                monitor.AfterTransactionExit = () =>
                {
                    monitor.AfterTransactionExit = null;
                    reached.Set();
                    Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
                };
                var closing = Task.Run(db.Dispose);
                try
                {
                    Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
                    iterator.Dispose();
                    reader.Dispose();
                    tx.Dispose();
                    tx.Dispose();
                }
                finally { resume.Set(); }
                await closing;
                Assert.Equal(LiteTransactionState.RolledBack, tx.State);
            }
            Verify(settings, new[] { 1 });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Disposing_healthy_handle_after_peer_fatal_failure_does_not_repeat_peer_error(string password)
        {
            using var file = new TempFile();
            using var log = new FailingLog(FileHelper.GetLogFile(file));
            using var data = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log,
                Password = password, DurableCommits = true }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db);
                var healthy = db.BeginTransaction();
                healthy.GetCollection("other").Insert(Row(3));
                using var failed = db.BeginTransaction();
                failed.GetCollection("rows").Insert(Row(2));
                log.Fail = true;
                Assert.Same(log.Failure, Assert.Throws<IOException>(failed.Commit));
                Assert.True(log.Reached);
                Assert.Equal(LiteTransactionState.Indeterminate, failed.State);
                healthy.Dispose();
                healthy.Dispose();
                Assert.Equal(LiteTransactionState.RolledBack, healthy.State);
            }
            log.Fail = false;
            // Caller-owned streams also retain platform file-sharing locks.
            log.Dispose();
            data.Dispose();
            // Failure happens before writing any peer WAL bytes; both pending writes are absent.
            Verify(new ConnectionString { Filename = file, Password = password }, new[] { 1 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reentrant_disposal_is_rejected_without_waiting_or_aborting(bool shared)
        {
            using var file = new TempFile();
            var settings = Settings(file, shared);
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection("rows");
                var reader = rows.Query().ExecuteReader();
                var iterator = rows.FindAll().GetEnumerator();
                Assert.True(iterator.MoveNext());
                IEnumerable<BsonDocument> Input()
                {
                    Assert.Throws<InvalidOperationException>(tx.Dispose);
                    Assert.Throws<InvalidOperationException>(reader.Dispose);
                    Assert.Throws<InvalidOperationException>(iterator.Dispose);
                    yield return Row(2);
                }
                rows.Insert(Input());
                Assert.Equal(LiteTransactionState.Active, tx.State);
                reader.Dispose();
                iterator.Dispose();
                tx.Commit();
            }
            Verify(settings, new[] { 1, 2 });
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("secret", 0)]
        [InlineData(null, 1)]
        [InlineData("secret", 1)]
        [InlineData(null, 2)]
        [InlineData("secret", 2)]
        public void Deferred_fatal_teardown_does_not_repeat_peer_error_on_healthy_disposal(string password, int cleanupOrder)
        {
            using var file = new TempFile();
            using var enteredRead = new ManualResetEventSlim();
            using var releaseRead = new ManualResetEventSlim();
            var holdRead = false;
            using var log = new FailingLog(FileHelper.GetLogFile(file));
            using var data = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log,
                Password = password, DurableCommits = true,
                ReadTransform = (collection, value) =>
                {
                    if (holdRead && collection == "sentinel")
                    {
                        enteredRead.Set();
                        if (!releaseRead.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("read barrier");
                    }
                    return value;
                } }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Seed(db);
                var healthy = db.BeginTransaction();
                healthy.GetCollection("other").Insert(Row(3));
                var healthyReader = healthy.GetCollection("other").Query().ExecuteReader();
                var healthyIterator = healthy.GetCollection("other").FindAll().GetEnumerator();
                Assert.True(healthyReader.Read());
                Assert.True(healthyIterator.MoveNext());
                using var failed = db.BeginTransaction();
                failed.GetCollection("rows").Insert(Row(2));
                holdRead = true;
                var concurrentRead = Task.Run(() => db.GetCollection("sentinel").FindById(9));
                Assert.True(enteredRead.Wait(TimeSpan.FromSeconds(5)));
                try
                {
                    log.Fail = true;
                    Assert.Same(log.Failure, Assert.Throws<IOException>(failed.Commit));
                    Assert.True(log.Reached);
                    Assert.Equal(LiteTransactionState.Indeterminate, failed.State);
                    if (cleanupOrder == 1)
                    {
                        healthyReader.Dispose();
                        healthyIterator.Dispose();
                    }
                    // Explicit completion still reports the fatal cause; only disposal
                    // hands cleanup to the engine's already-published fatal stop.
                    if (cleanupOrder == 2) Assert.Throws<IOException>(healthy.Rollback);
                    else healthy.Dispose();
                    healthyReader.Dispose();
                    healthyIterator.Dispose();
                    healthy.Dispose();
                    Assert.Equal(cleanupOrder == 2 ? LiteTransactionState.Failed : LiteTransactionState.RolledBack, healthy.State);
                }
                finally
                {
                    releaseRead.Set();
                    try { concurrentRead.GetAwaiter().GetResult(); } catch (IOException) { }
                }
            }
            log.Fail = false;
            // Caller-owned streams also retain platform file-sharing locks.
            log.Dispose();
            data.Dispose();
            // Failure happens before writing any peer WAL bytes; both pending writes are absent.
            Verify(new ConnectionString { Filename = file, Password = password }, new[] { 1 });
        }

        private static LiteEngine Core(ILiteTransaction tx) => ((TransactionResources)typeof(LiteTransaction)
            .GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tx)).Engine;
        private static ConnectionString Settings(TempFile file, bool shared) => new ConnectionString
        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };
        private static void Seed(LiteDatabase db)
        {
            db.GetCollection("rows").Insert(Row(1));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(9));
            db.Checkpoint();
        }
        private static void Verify(ConnectionString settings, int[] ids)
        {
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(settings);
                var rows = cold.GetCollection("rows");
                Assert.Equal(ids, rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
                Assert.Equal(ids, rows.Find(Query.GTE("value", 10)).Select(x => x["_id"].AsInt32).OrderBy(x => x));
                Assert.Equal(0, cold.GetCollection("other").Count());
                Assert.Equal(90, cold.GetCollection("sentinel").FindById(9)["value"].AsInt32);
            }
        }
        private sealed class FailingLog : FileStream
        {
            internal bool Fail, Reached;
            internal readonly IOException Failure = new IOException("peer WAL write failed");
            internal FailingLog(string file) : base(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) { }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Fail) { Reached = true; throw Failure; }
                base.Write(buffer, offset, count);
            }
        }
    }
}
