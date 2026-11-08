using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleCloseMaintenance_Tests
    {
        internal static ILiteEngine Engine(string file, int mode) => mode == 0
            ? (ILiteEngine)new LiteEngine(file)
            : new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mode == 2 });

        internal static bool Closing(LiteDatabase db) =>
            (bool)typeof(TransactionHandles).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(db.TransactionHandles);
        internal static int Pending(LiteDatabase db) =>
            (int)typeof(TransactionHandles).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(db.TransactionHandles);

        [Theory]
        [InlineData(0)] [InlineData(1)]
        public void Close_drains_executing_call_invalidates_cursor_and_preserves_caller_engine(int mode) => Drain(mode);

        internal static void Drain(int mode)
        {
            using var file = new TempFile();
            using var engine = Engine(file, mode);
            var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
            db.GetCollection("rows").EnsureIndex("value");
            var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            using var reader = rows.Query().ExecuteReader();
            Assert.True(reader.Read());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IEnumerable<BsonDocument> Input()
            {
                Assert.Throws<InvalidOperationException>(db.Dispose);
                Assert.False(Closing(db));
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                yield return new BsonDocument { ["_id"] = 2, ["value"] = 20 };
            }
            var write = Task.Run(() => rows.Insert(Input()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var close = Task.Run(db.Dispose);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => Closing(db), TimeSpan.FromSeconds(5)));
                Assert.False(close.IsCompleted);
                Assert.Throws<ObjectDisposedException>(() => db.BeginTransaction());
                Assert.Throws<ObjectDisposedException>(() => db.GetCollection("rows").Count());
            }
            finally { release.Set(); }
            Assert.True(Task.WaitAll(new Task[] { write, close }, TimeSpan.FromSeconds(15)));
            Assert.Equal(LiteTransactionState.RolledBack, tx.State);
            Assert.Throws<ObjectDisposedException>(() => reader.Read());
            Assert.Equal(0, db.TransactionHandles.ActiveCount);
            // disposeOnClose:false settles database ownership but leaves this engine usable.
            using (var peer = new LiteDatabase(engine, disposeOnClose: false))
            {
                Assert.Equal(new[] { 1 }, peer.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
                Assert.Equal(1, peer.GetCollection("rows").Count(Query.EQ("value", 10)));
                peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            }
            tx.Dispose(); db.Dispose(); engine.Dispose();
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file);
                Assert.Equal(new[] { 1, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
        }

        [Theory]
        [InlineData(0)] [InlineData(1)]
        public void Live_database_retains_abandoned_handle_until_explicit_close(int mode) => Retained(mode);

        internal static void Retained(int mode)
        {
            using var file = new TempFile();
            using var engine = Engine(file, mode);
            var db = new LiteDatabase(engine);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var weak = Drop(db);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.True(weak.IsAlive);
            Assert.Equal(1, db.TransactionHandles.ActiveCount);
            var tx = (ILiteTransaction)weak.Target;
            Assert.Equal(LiteTransactionState.Active, tx.State);
            db.Dispose();
            Assert.Equal(LiteTransactionState.RolledBack, tx.State);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
            GC.KeepAlive(db);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference Drop(LiteDatabase db)
        {
            var tx = db.BeginTransaction();
            tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            return new WeakReference(tx);
        }

        [Theory]
        [InlineData(1)]
        public void Close_cancels_local_begin_and_rolls_back_idle_owner(int mode) => LocalBegin(mode);

        internal static void LocalBegin(int mode)
        {
            using var file = new TempFile();
            using var engine = Engine(file, mode);
            var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var idle = db.BeginTransaction();
            idle.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            var begin = Task.Run(() => Record.Exception(() => db.BeginTransaction()));
            Assert.True(SpinWait.SpinUntil(() => Pending(db) == 1, TimeSpan.FromSeconds(5)));
            var close = Task.Run(db.Dispose);
            Assert.True(Task.WaitAll(new Task[] { begin, close }, TimeSpan.FromSeconds(5)));
            Assert.IsType<ObjectDisposedException>(begin.Result);
            Assert.Equal(LiteTransactionState.RolledBack, idle.State);
            Assert.Equal(0, Pending(db));
            using var peer = new LiteDatabase(engine, disposeOnClose: false);
            using var next = peer.BeginTransaction();
            Assert.Equal(new[] { 1 }, next.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
            next.Commit();
        }

        [Fact]
        public void Close_cancels_Direct_begin_behind_exclusive_admission_without_waiting_on_its_owner()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            var db = new LiteDatabase(engine, disposeOnClose: false);
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            using var exclusive = new ManualResetEventSlim();
            using var entering = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = Task.Run(() =>
            {
                locker.EnterExclusive();
                try { exclusive.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
                finally { locker.ExitExclusive(); }
            });
            Assert.True(exclusive.Wait(TimeSpan.FromSeconds(5)));
            locker.BeforeTransactionAdmission = entering.Set;
            var begin = Task.Run(() => Record.Exception(() => db.BeginTransaction()));
            Assert.True(entering.Wait(TimeSpan.FromSeconds(5)));
            var close = Task.Run(db.Dispose);
            try
            {
                Assert.True(Task.WaitAll(new Task[] { begin, close }, TimeSpan.FromSeconds(5)));
                Assert.IsType<ObjectDisposedException>(begin.Result);
                Assert.Equal(0, Pending(db));
                Assert.Equal(0, locker.TransactionsCount);
                Assert.Empty(engine.GetMonitor().GetTransactionsSnapshot());
            }
            finally { release.Set(); }
            Assert.True(owner.Wait(TimeSpan.FromSeconds(5)));
            locker.BeforeTransactionAdmission = null;
        }

        [Theory]
        [InlineData(0)] [InlineData(1)]
        public void Raw_engine_close_waits_for_executing_handle_before_storage_release(int mode) => RawClose(mode);

        internal static void RawClose(int mode)
        {
            using var file = new TempFile();
            using var engine = Engine(file, mode);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IEnumerable<BsonDocument> Input()
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                yield return new BsonDocument { ["_id"] = 2 };
            }
            var write = Task.Run(() => Record.Exception(() => rows.Insert(Input())));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var started = new ManualResetEventSlim();
            var close = Task.Run(() => { started.Set(); engine.Dispose(); });
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(close.Wait(TimeSpan.FromMilliseconds(100)));
            }
            finally { release.Set(); }
            Assert.True(Task.WaitAll(new Task[] { write, close }, TimeSpan.FromSeconds(10)));
            Assert.Null(write.Result);
            var failure = Record.Exception(tx.Commit);
            Assert.NotNull(failure);
            Assert.NotEqual(LiteTransactionState.Committed, tx.State);
            tx.Dispose(); db.Dispose();
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Begin_publication_after_close_rolls_back_without_returning_a_handle()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            var db = new LiteDatabase(engine, disposeOnClose: false);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            locker.BeforeTransactionAdmission = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
            var begin = Task.Run(() => Record.Exception(() => db.BeginTransaction()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var close = Task.Run(db.Dispose);
            try { Assert.True(SpinWait.SpinUntil(() => Closing(db), TimeSpan.FromSeconds(5))); }
            finally { release.Set(); }
            Assert.True(Task.WaitAll(new Task[] { begin, close }, TimeSpan.FromSeconds(10)));
            Assert.IsType<ObjectDisposedException>(begin.Result);
            Assert.Equal(0, Pending(db));
            Assert.Equal(0, db.TransactionHandles.ActiveCount);
            Assert.Empty(engine.GetMonitor().GetTransactionsSnapshot());
            locker.BeforeTransactionAdmission = null;
            using var peer = new LiteDatabase(engine, disposeOnClose: false);
            using var next = peer.BeginTransaction();
            next.Commit();
        }
    }
}
