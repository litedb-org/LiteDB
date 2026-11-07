using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleDirectApi_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Legacy_owner_on_same_thread_refuses_handle_with_141(bool peer)
        {
            using var file = new TempFile();
            using var a = new LiteDatabase(file);
            using var b = peer ? new LiteDatabase(file) : null;
            Assert.True(a.BeginTrans());
            a.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1 });
            Assert.Equal(141, Assert.Throws<LiteException>(() => (b ?? a).BeginTransaction()).ErrorCode);
            Assert.True(a.Commit());
            using var next = (b ?? a).BeginTransaction();
            Assert.NotNull(next.GetCollection("sentinel").FindById(1));
            next.Commit();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Memory_and_caller_stream_handles_handoff_without_creator(bool stream)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var db = stream ? new LiteDatabase(data, logStream: log) : new LiteDatabase(":memory:"))
            {
                ILiteTransaction tx = null;
                TransactionHandle_Tests.OnThread(() => tx = db.BeginTransaction());
                using (tx)
                {
                    var rows = tx.GetCollection("rows");
                    TransactionHandle_Tests.OnThread(() => rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 }));
                    TransactionHandle_Tests.OnThread(() => Assert.True(rows.EnsureIndex("value")));
                    TransactionHandle_Tests.OnThread(() => Assert.Equal(42, rows.FindById(1)["value"].AsInt32));
                    TransactionHandle_Tests.OnThread(tx.Commit);
                    Assert.Equal(LiteTransactionState.Committed, tx.State);
                    Assert.Throws<InvalidOperationException>(() => rows.Insert(new BsonDocument { ["_id"] = 2 }));
                }
                using var aborted = db.BeginTransaction();
                TransactionHandle_Tests.OnThread(() => aborted.GetCollection("rows").Delete(1));
                TransactionHandle_Tests.OnThread(aborted.Rollback);
                Assert.NotNull(db.GetCollection("rows").FindById(1));
            }
            if (stream)
            {
                Assert.True(data.CanRead);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var reopened = new LiteDatabase(data, logStream: log);
                    Assert.Equal(42, reopened.GetCollection("rows").FindById(1)["value"].AsInt32);
                }
            }
        }

        [Fact]
        public void Competing_handle_collection_write_obeys_timeout_and_preserves_holder()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            db.Timeout = TimeSpan.FromSeconds(1);
            db.GetCollection("rows").EnsureIndex("value");
            using var holder = db.BeginTransaction();
            holder.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var timer = Stopwatch.StartNew();
            TransactionHandle_Tests.OnThread(() =>
            {
                using var waiter = db.BeginTransaction();
                Assert.Equal(120, Assert.Throws<LiteException>(() => waiter.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 })).ErrorCode);
                Assert.Equal(LiteTransactionState.Failed, waiter.State);
            });
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
            holder.Commit();
            Assert.Equal(new[] { 1 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Shared_is_disabled_and_non_provider_extension_has_no_side_effects()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            Assert.Contains("Shared", Assert.Throws<NotSupportedException>(() => db.BeginTransaction()).Message);
            ILiteDatabase provider = new NonProviderDatabase();
            Assert.Throws<NotSupportedException>(() => provider.BeginTransaction());
            Assert.Empty(((NonProviderDatabase)provider).Calls);
            Assert.Throws<ArgumentNullException>(() => ((ILiteDatabase)null).BeginTransaction());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Public_completion_preserves_known_outcome_when_cleanup_throws(bool commit)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 7 });
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                var monitor = (TransactionMonitor)typeof(LiteEngine).GetField("_monitor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                var failure = new InvalidOperationException("completion cleanup");
                monitor.AfterTransactionExit = () => throw failure;
                try
                {
                    Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { if (commit) tx.Commit(); else tx.Rollback(); }));
                    Assert.Equal(commit ? LiteTransactionState.Committed : LiteTransactionState.RolledBack, tx.State);
                    Assert.Throws<InvalidOperationException>(() => tx.GetCollection("escape"));
                }
                finally { monitor.AfterTransactionExit = null; }
            }
            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase(file);
                Assert.NotNull(db.GetCollection("sentinel").FindById(7));
                Assert.Equal(commit ? 1 : 0, db.GetCollection("rows").Count());
                Assert.False(db.CollectionExists("escape"));
            }
        }

        [Fact]
        public void Legacy_attributes_are_warning_only_on_both_client_contracts()
        {
            foreach (var type in new[] { typeof(LiteDatabase), typeof(ILiteDatabase) })
            foreach (var name in new[] { "BeginTrans", "Commit", "Rollback" })
            {
                var attribute = type.GetMethod(name).GetCustomAttribute<ObsoleteAttribute>();
                Assert.NotNull(attribute);
                Assert.False(attribute.IsError);
                Assert.Contains("BeginTransaction()", attribute.Message);
            }
            Assert.Null(typeof(ILiteDatabase).GetMethod("BeginTransaction"));
            Assert.Null(typeof(ILiteEngine).GetMethod("BeginTransaction"));
        }
    }
}
