using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A Shared begin interrupted while it waits leaks neither the local handle queue nor the
    /// native writer ownership its holder acquires afterwards.
    /// </summary>
    public class TransactionHandleInterruptedBegin_Tests
    {
        public enum Stage { NativeWait, LocalQueue }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static SharedEngine EngineOf(LiteDatabase db) => SharedHandleQueue.EngineOf(db);

#pragma warning disable CS0618
        [Theory]
        [InlineData(Stage.NativeWait)]
        [InlineData(Stage.LocalQueue)]
        public void Interrupted_shared_begin_releases_everything_it_would_acquire(Stage stage)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(Row(1));
            var shared = new ConnectionString { Filename = file, Connection = ConnectionType.Shared };
            // Windows keeps Shared file handles open: close every connection before the
            // Direct cold reopen below.
            using (var owner = new LiteDatabase(shared))
            using (var db = new LiteDatabase(shared))
            {
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                // The begin waits natively behind a legacy owner, or in the local queue behind a handle.
                Exception holdingError = null;
                var holding = new Thread(() => holdingError = Record.Exception(() =>
                {
                    ILiteTransaction tx = null;
                    try
                    {
                        if (stage == Stage.NativeWait) owner.BeginTrans(); else tx = owner.BeginTransaction();
                        (tx?.GetCollection("rows") ?? owner.GetCollection("rows")).Insert(Row(2));
                    }
                    finally { held.Set(); }
                    release.Wait(TimeSpan.FromSeconds(20));
                    if (tx == null) owner.Commit(); else tx.Commit();
                }));
                holding.Start();
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                Exception beginError = null;
                var begin = new Thread(() =>
                {
                    try { db.BeginTransaction().Dispose(); }
                    catch (Exception error) { beginError = error; }
                });
                begin.Start();
                if (stage == Stage.NativeWait)
                {
                    // The begin's holder is queued at the file's turnstile, waiting for the owner.
                    var turnstile = TransactionHandleSharedCallback_Tests.Turnstile(EngineOf(db));
                    Assert.True(SpinWait.SpinUntil(turnstile.HasWaiter, TimeSpan.FromSeconds(10)));
                }
                // The begin waits in the local handle queue behind the other handle.
                else Assert.True(SharedHandleQueue.WaitForQueuedBegin(SharedHandleQueue.Of(db), TimeSpan.FromSeconds(10)));
                Assert.True(SpinWait.SpinUntil(() => (begin.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                begin.Interrupt();
                Assert.True(begin.Join(TimeSpan.FromSeconds(10)));
                Assert.IsType<ThreadInterruptedException>(beginError);
                release.Set();
                Assert.True(holding.Join(TimeSpan.FromSeconds(10)));
                Assert.Null(holdingError);
                // Once the holder (if any) acquires and releases, other connections write and begin again.
                using var probe = new LiteDatabase(shared);
                var write = Task.Run(() => probe.GetCollection("rows").Insert(Row(3)));
                Assert.True(write.Wait(TimeSpan.FromSeconds(20)), "The interrupted begin's holder kept writer ownership.");
                var next = Task.Run(() => { using var tx = probe.BeginTransaction(); tx.GetCollection("rows").Insert(Row(4)); tx.Commit(); });
                Assert.True(next.Wait(TimeSpan.FromSeconds(20)), "The interrupted begin kept the local handle queue.");
                Assert.Equal(1, SharedHandleQueue.Of(db).CurrentCount);
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 2, 3, 4 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }
#pragma warning restore CS0618
    }
}
