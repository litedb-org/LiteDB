using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    internal static class ReuseAccess
    {
        internal static LiteEngine Core(ILiteTransaction tx) => ((LiteTransaction)tx).Storage;

        internal static object Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

        internal static string MutexName(SharedEngine shared) => (string)Field(shared, "_mutexName");

        internal static bool IsDisposed(SharedEngine shared) => (int)Field(shared, "_disposed") != 0;

        internal static int Discards(SharedEngine shared, string reason) =>
            shared.TransactionChildDiscards.TryGetValue(reason, out var count) ? count : 0;
    }

    /// <summary>Reuse of Shared holder threads and wrappers (#3083): counts, ownership release, #3081 guards.</summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleReuse_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Sequential_handles_reuse_one_holder_thread_and_one_wrapper_with_fresh_cores(string password)
        {
            const int handles = 8;
            using var file = new TempFile();
            Seed(file, password);
            using (var pool = new TransactionHolderScheduler_Tests.PoolScope())
            using (var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            {
                int created = SharedHolderScheduler.Created, reused = SharedHolderScheduler.Reused;
                var cores = new HashSet<LiteEngine>();
                SharedEngine child = null;
                Thread holder = null;
                for (var i = 0; i < handles; i++)
                {
                    using (var tx = db.BeginTransaction())
                    {
                        Assert.True(cores.Add(ReuseAccess.Core(tx)), "A handle reused an earlier storage core.");
                        Assert.Equal(i + 1, tx.GetCollection("rows").Count());
                        tx.GetCollection("rows").Insert(Row(i + 2));
                        tx.Commit();
                    }
                    var cached = shared.CachedTransactionChild;
                    Assert.NotNull(cached);
                    if (child == null) child = cached;
                    Assert.Same(child, cached);
                    // Between handles the wrapper keeps no core and no ownership.
                    Assert.Null(ReuseAccess.Field(cached, "_engine"));
                    Assert.False(cached.MutexOwner.IsHeld);
                    // A barrier, not a race: the next begin finds this holder in the pool.
                    TransactionHolderScheduler_Tests.Idle(cached.HolderThread);
                    if (holder == null) holder = cached.HolderThread;
                    Assert.Same(holder, cached.HolderThread);
                }
                Assert.Equal(1, SharedHolderScheduler.Created - created);
                Assert.Equal(handles - 1, SharedHolderScheduler.Reused - reused);
                Assert.Equal(1, shared.TransactionChildrenCreated);
                Assert.Equal(handles - 1, shared.TransactionChildrenReused);
                Assert.Empty(shared.TransactionChildDiscards);
                Assert.Equal(handles, child.EngineOpens);
            }
            Verify(file, password, Enumerable.Range(1, handles + 1).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Another_connection_writes_promptly_after_each_reused_handle(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var pool = new TransactionHolderScheduler_Tests.PoolScope())
            using (var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            // A writer that does not get the native mutex within 200 ms fails with LOCK_TIMEOUT.
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password, timeout: TimeSpan.FromMilliseconds(200)))))
            {
                var name = ReuseAccess.MutexName(shared);
                for (var round = 1; round <= 50; round++)
                {
                    using (var tx = db.BeginTransaction())
                    {
                        tx.GetCollection("rows").Insert(Row(round + 1));
                        tx.Commit();
                    }
                    var cached = shared.CachedTransactionChild;
                    Assert.NotNull(cached);
                    Assert.False(cached.MutexOwner.IsHeld);
                    var id = round;
                    TransactionHandle_Tests.OnThread(() => peer.GetCollection("peer").Insert(Row(id)));
                    // The raw OS mutex is free too: neither held nor abandoned by a pooled holder.
                    TransactionHandle_Tests.OnThread(() =>
                    {
                        using var raw = SharedMutexFactory.Create(name);
                        Assert.True(raw.WaitOne(0), "A pooled holder thread still owns the writer mutex.");
                        raw.ReleaseMutex();
                    });
                }
                Assert.Equal(1, shared.TransactionChildrenCreated);
                Assert.Equal(49, shared.TransactionChildrenReused);
                Assert.Equal(50, peer.GetCollection("peer").Count());
            }
            Verify(file, password, Enumerable.Range(1, 51).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Timed_out_begin_caches_nothing_and_counts_one_timeout(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            var shared = new SharedEngine(Settings(file, password, timeout: TimeSpan.FromMilliseconds(300)));
            using (var db = new LiteDatabase(shared))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                using (var warm = db.BeginTransaction()) warm.Commit();
                var cached = shared.CachedTransactionChild;
                Assert.NotNull(cached);
                using var release = new ManualResetEventSlim();
                using var held = new ManualResetEventSlim();
                var holder = Unmarked(() =>
                {
#pragma warning disable CS0618
                    other.BeginTrans();
                    other.GetCollection("rows").Insert(Row(2));
                    held.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    other.Commit();
#pragma warning restore CS0618
                });
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                var timeout = Assert.Throws<LiteException>(() => db.BeginTransaction());
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.TimedOut);
                // The waiting holder checked the wrapper out; its failed open discarded it.
                Assert.Null(shared.CachedTransactionChild);
                Assert.True(ReuseAccess.IsDisposed(cached));
                Assert.Equal(1, ReuseAccess.Discards(shared, "error"));
                release.Set();
                Assert.True(holder.Wait(TimeSpan.FromSeconds(20)));
                using (var retry = db.BeginTransaction())
                {
                    Assert.NotNull(retry.GetCollection("rows").FindById(2));
                    retry.GetCollection("rows").Insert(Row(3));
                    retry.Commit();
                }
                Assert.NotNull(shared.CachedTransactionChild);
                Assert.NotSame(cached, shared.CachedTransactionChild);
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.TimedOut);
            }
            Verify(file, password, 1, 2, 3);
        }

        [Fact]
        public void Second_begin_of_the_flow_is_refused_only_while_the_reused_handle_is_active()
        {
            using var file = new TempFile();
            Seed(file);
            var shared = new SharedEngine(Settings(file, grace: Immediately));
            using (var db = new LiteDatabase(shared))
            {
                var name = ReuseAccess.MutexName(shared);
                using (var warm = db.BeginTransaction()) warm.Commit();
                for (var round = 2; round <= 3; round++)
                {
                    using (var tx = db.BeginTransaction())
                    {
                        Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
                        tx.GetCollection("rows").Insert(Row(round));
                        tx.Commit();
                    }
                    // The completed handle unregistered its activity: no stale owner remains.
                    Assert.Null(SharedHandleRegistry.Owner(name));
                    Assert.Equal(SharedWriterOwner.Unknown, db.GetSharedWaitDiagnostics().Owner);
                }
                using (var after = db.BeginTransaction()) after.Commit();
                Assert.Equal(1, shared.TransactionChildrenCreated);
                Assert.Equal(3, shared.TransactionChildrenReused);
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Busy_holders_of_several_databases_are_never_capped()
        {
            var files = Enumerable.Range(0, 4).Select(_ => new TempFile()).ToArray();
            var databases = files.Select(file => new LiteDatabase(new SharedEngine(new EngineSettings { Filename = file }))).ToArray();
            using var pool = new TransactionHolderScheduler_Tests.PoolScope();
            try
            {
                var created = SharedHolderScheduler.Created;
                // Four handles of four databases are active at once: four busy holders.
                var handles = databases.Select(db => db.BeginTransaction()).ToArray();
                for (var i = 0; i < handles.Length; i++) handles[i].GetCollection("rows").Insert(Row(i + 1));
                // The pool started empty and no holder was capped or shared: four were created.
                Assert.Equal(4, SharedHolderScheduler.Created - created);
                foreach (var handle in handles) handle.Commit();
                foreach (var handle in handles) handle.Dispose();
                Assert.True(SharedHolderScheduler.IdleCount <= SharedHolderScheduler.MaximumIdle);
            }
            finally
            {
                foreach (var db in databases) db.Dispose();
            }
            for (var i = 0; i < files.Length; i++)
            {
                using (var cold = new LiteDatabase(files[i].Filename)) Assert.Equal(i + 1, cold.GetCollection("rows").FindAll().Single()["_id"].AsInt32);
                files[i].Dispose();
            }
        }
    }
}
