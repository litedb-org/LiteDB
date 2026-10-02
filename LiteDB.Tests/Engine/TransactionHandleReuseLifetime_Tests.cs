using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>Lifetime of reused Shared holder wrappers (#3083): disposal, collection and finalization.</summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleReuseLifetime_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Disposing_the_connection_disposes_the_cached_wrapper_and_refuses_a_late_return(string password, bool activeHandle)
        {
            using var file = new TempFile();
            Seed(file, password);
            var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password });
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            {
                using (var warm = db.BeginTransaction()) warm.Commit();
                var cached = shared.CachedTransactionChild;
                Assert.NotNull(cached);
                ILiteTransaction active = null;
                if (activeHandle)
                {
                    // The handle checks the wrapper out; the cache is empty while it runs.
                    active = db.BeginTransaction();
                    active.GetCollection("rows").Insert(Row(2));
                    Assert.Null(shared.CachedTransactionChild);
                }
                // A caller-owned engine disposed underneath does not end a handle of its database.
                shared.Dispose();
                Assert.Null(shared.CachedTransactionChild);
                Assert.Equal(!activeHandle, ReuseAccess.IsDisposed(cached));
                if (activeHandle)
                {
                    active.Commit();
                    active.Dispose();
                    // The late return was refused: the wrapper is disposed, not cached.
                    Assert.Null(shared.CachedTransactionChild);
                    Assert.True(ReuseAccess.IsDisposed(cached));
                    Assert.Equal(1, ReuseAccess.Discards(shared, "disposed-parent"));
                }
            }
            if (activeHandle) Verify(file, password, 1, 2);
            else Verify(file, password, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Idle_worker_roots_no_database_handle_read_policy_or_ambient_value(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using var pool = new TransactionHolderScheduler_Tests.PoolScope();
            WeakReference[] graph = null;
            Thread worker = null;
            TransactionHandle_Tests.OnThread(() => graph = CompleteAndDrop(file, password, out worker));
            TransactionHolderScheduler_Tests.Idle(worker);
            for (var attempt = 0; attempt < 10 && graph.Any(reference => reference.IsAlive); attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(graph, reference => Assert.False(reference.IsAlive));
            // The worker that ran the handles is still idle in the pool: it alone roots nothing.
            Assert.True(SharedHolderScheduler.IsIdle(worker));
            Verify(file, password, 1, 2, 3);
        }

        private sealed class ApplicationState
        {
            internal LiteDatabase Database;
            internal ILiteTransaction Active;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CompleteAndDrop(string file, string password, out Thread worker)
        {
            var state = new ApplicationState();
            var ambient = new AsyncLocal<ApplicationState> { Value = state };
            Func<string, BsonValue, BsonValue> policy = (collection, value) => { GC.KeepAlive(state); return value; };
            var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password, ReadTransform = policy });
            var db = state.Database = new LiteDatabase(shared);
            ILiteTransaction last = null;
            for (var id = 2; id <= 3; id++)
            {
                last = db.BeginTransaction();
                last.GetCollection("rows").Insert(Row(id));
                Assert.Equal(id, last.GetCollection("rows").Count());
                last.Commit();
            }
            var child = shared.CachedTransactionChild;
            Assert.NotNull(child);
            Assert.Equal(1, shared.TransactionChildrenReused);
            worker = child.HolderThread;
            // Neither the database nor its connection is disposed: only collection can release them.
            GC.KeepAlive(ambient.Value);
            return new[] { new WeakReference(db), new WeakReference(shared), new WeakReference(last), new WeakReference(policy),
                new WeakReference(state), new WeakReference(child) };
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Abandoned_active_handle_and_database_release_ownership_and_the_wrapper_is_not_cached(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using var pool = new TransactionHolderScheduler_Tests.PoolScope();
            var orphaned = SharedEngine.TransactionChildrenOrphaned;
            WeakReference[] graph = null;
            TransactionHandle_Tests.OnThread(() => graph = AbandonActive(file, password));
            using var peer = new LiteDatabase(new SharedEngine(Settings(file, password, timeout: TimeSpan.FromMilliseconds(100))));
            var written = false;
            // Finalization is the only thing that can release the abandoned handle's ownership.
            for (var attempt = 0; attempt < 200 && !(written && graph.All(reference => !reference.IsAlive)); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (!written)
                    TransactionHandle_Tests.OnThread(() =>
                    {
                        try { peer.GetCollection("rows").Insert(Row(4)); written = true; }
                        catch (LiteException timeout) when (timeout.ErrorCode == LiteException.LOCK_TIMEOUT) { }
                    });
            }
            Assert.True(written, "A peer writer never proceeded after the abandoned handle was collected.");
            Assert.All(graph, reference => Assert.False(reference.IsAlive));
            // The holder found its connection collected and disposed the wrapper instead of caching it.
            Assert.True(SpinWait.SpinUntil(() => SharedEngine.TransactionChildrenOrphaned - orphaned == 1, TimeSpan.FromSeconds(10)));
            peer.Dispose();
            Verify(file, password, 1, 2, 4);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] AbandonActive(string file, string password)
        {
            var state = new ApplicationState();
            // The begin's flow carries the application state; a holder running in it would root it.
            var ambient = new AsyncLocal<ApplicationState> { Value = state };
            Func<string, BsonValue, BsonValue> policy = (collection, value) => { GC.KeepAlive(state); return value; };
            var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password, ReadTransform = policy,
                TransactionPageLimit = 1 });
            var db = state.Database = new LiteDatabase(shared);
            using (var completed = db.BeginTransaction())
            {
                completed.GetCollection("rows").Insert(Row(2));
                completed.Commit();
            }
            var child = shared.CachedTransactionChild;
            Assert.NotNull(child);
            // The next handle reuses the wrapper and is abandoned while it owns the writer mutex,
            // after spilling uncommitted pages to the WAL.
            state.Active = db.BeginTransaction();
            state.Active.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 30, ["payload"] = new string('x', 50000) });
            Assert.Null(shared.CachedTransactionChild);
            GC.KeepAlive(ambient.Value);
            return new[] { new WeakReference(db), new WeakReference(shared), new WeakReference(state.Active),
                new WeakReference(policy), new WeakReference(state) };
        }
    }
}
