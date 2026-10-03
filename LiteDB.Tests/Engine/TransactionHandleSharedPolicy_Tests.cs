using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleSharedPolicy_Tests
    {
        [Theory]
        [InlineData(":memory:")]
        [InlineData(":temp:")]
        public void Shared_private_storage_cannot_claim_persistent_handle_commits(string filename)
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                // Framework can reject private storage while constructing its path-based
                // reader registry; other runtimes reach the explicit handle capability guard.
                using var db = new LiteDatabase(new SharedEngine(new EngineSettings { Filename = filename }));
                using var tx = db.BeginTransaction();
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Handle_degradation_survives_reopen_without_suppressing_later_device_sync(string password)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Shared });
            db.CheckpointSize = 0;
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.Equal(1, CommitWithSyncProbe(tx, reject: true));
            }
            Assert.False(IsDurable(db));
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                Assert.True(CommitWithSyncProbe(tx, reject: false) > 0);
            }
            Assert.False(IsDurable(db));
            db.Dispose();
            using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            Assert.Equal(2, cold.GetCollection("rows").Count());
            Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
        }

        [Fact]
        public void Callback_enabled_handles_use_one_lifetime_holder_without_lingering_mutex_helpers()
        {
            using var file = new TempFile();
            var callbacks = 0;
            using var shared = new SharedEngine(new EngineSettings { Filename = file,
                ReadTransform = (collection, value) => { callbacks++; return value; } });
            using var db = new LiteDatabase(shared, disposeOnClose: false);
            for (var i = 0; i < 20; i++)
            {
                using var tx = db.BeginTransaction();
                var resources = Resources(tx);
                var release = (Action)typeof(TransactionResources).GetField("_release", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(resources);
                var child = (SharedEngine)release.Target.GetType().GetField("_child", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(release.Target);
                Assert.False(child.MutexOwner.HasHolderThread);
                tx.GetCollection("rows").Upsert(new BsonDocument { ["_id"] = 1 });
                Assert.NotNull(tx.GetCollection("rows").FindById(1));
                TransactionHandle_Tests.OnThread(tx.Commit);
                Assert.False(child.MutexOwner.HasHolderThread);
            }
            Assert.True(callbacks >= 20);
        }

        private static int CommitWithSyncProbe(ILiteTransaction tx, bool reject)
        {
            var attempted = 0;
            var caller = Thread.CurrentThread;
            var originalPhase = EngineState.SimulateProcessCrash;
            var originalSync = NativeFileSync.SimulateErrno;
            try
            {
                // Writer initialization (including the AES preamble) precedes this
                // boundary. This window syncs only the confirmed WAL (directory sync
                // has a separate hook). Handle-backed Windows streams have no filename.
                EngineState.SimulateProcessCrash = phase =>
                {
                    if (!ReferenceEquals(Thread.CurrentThread, caller)) return;
                    if (phase == "wal-before-durable-flush") NativeFileSync.SimulateErrno = path =>
                    {
                        if (!ReferenceEquals(Thread.CurrentThread, caller)) return 0;
                        return Interlocked.Increment(ref attempted) == 1 && reject ? 22 : 0;
                    };
                    if (phase == "wal-after-durable-flush") NativeFileSync.SimulateErrno = originalSync;
                };
                tx.Commit();
                return attempted;
            }
            finally
            {
                EngineState.SimulateProcessCrash = originalPhase;
                NativeFileSync.SimulateErrno = originalSync;
            }
        }

        private static bool IsDurable(LiteDatabase db) => db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static TransactionResources Resources(ILiteTransaction tx) => (TransactionResources)typeof(LiteTransaction)
            .GetField("_resources", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tx);
    }
}
