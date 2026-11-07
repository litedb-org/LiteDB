using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleDeadline_Tests
    {
        [Fact]
        public void Nested_stages_inherit_the_sampled_timeout_and_restore_thread_state()
        {
            var original = TimeSpan.FromSeconds(2);
            using (new TransactionWaitBudget(original))
            {
                using (new TransactionWaitBudget(TimeSpan.FromSeconds(10)))
                    Assert.Equal(original, TransactionWaitBudget.Timeout(TimeSpan.FromSeconds(1)));
                Assert.Equal(original, TransactionWaitBudget.Timeout(TimeSpan.FromSeconds(1)));
            }
            Assert.Equal(TimeSpan.FromSeconds(1), TransactionWaitBudget.Timeout(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void Forced_collection_stages_share_wait_budget_and_user_work_does_not_spend_it()
        {
            var first = new CollectionLock();
            var second = new CollectionLock();
            var holder = new object();
            var waiter = new object();
            Assert.True(first.TryEnter(holder, TimeSpan.Zero));
            Assert.True(second.TryEnter(holder, TimeSpan.Zero));
            using var entered = new ManualResetEventSlim();
            first.BeforeWait = entered.Set;
            var release = Task.Run(() =>
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Thread.Sleep(350);
                first.Exit(holder);
            });
            using (new TransactionWaitBudget(TimeSpan.FromMilliseconds(600)))
            {
                Assert.True(first.TryEnter(waiter, TimeSpan.FromSeconds(5)));
                var remaining = TransactionWaitBudget.Remaining(TimeSpan.FromSeconds(5));
                Assert.True(remaining < TimeSpan.FromMilliseconds(300));
                Thread.Sleep(350); // application work must not expire the remaining allowance
                Assert.Equal(remaining, TransactionWaitBudget.Remaining(TimeSpan.FromSeconds(5)));
                var secondHit = false;
                second.BeforeWait = () => secondHit = true;
                Assert.False(second.TryEnter(waiter, TimeSpan.FromSeconds(5)));
                Assert.True(secondHit);
                Assert.Equal(TimeSpan.Zero, TransactionWaitBudget.Remaining(TimeSpan.FromSeconds(5)));
                first.Exit(waiter);
            }
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            second.Exit(holder);
            Assert.True(second.TryEnter(waiter, TimeSpan.Zero));
            second.Exit(waiter);
        }

        [Fact]
        public void Admission_then_collection_use_one_budget_and_failed_begin_publishes_nothing()
        {
            var timeout = TimeSpan.FromMilliseconds(600);
            using var locker = new LockService(() => timeout);
            var holder = new object();
            var waiter = new object();
            using var exclusive = new ManualResetEventSlim();
            using var entering = new ManualResetEventSlim();
            locker.EnterTransaction(holder);
            locker.EnterLock("rows", holder);
            locker.ExitTransaction(holder);
            locker.BeforeTransactionAdmission = entering.Set;
            var owner = Task.Run(() =>
            {
                locker.EnterExclusive();
                exclusive.Set();
                Assert.True(entering.Wait(TimeSpan.FromSeconds(5)));
                Thread.Sleep(350);
                locker.ExitExclusive();
            });
            Assert.True(exclusive.Wait(TimeSpan.FromSeconds(5)));
            using (new TransactionWaitBudget(timeout))
            {
                locker.EnterTransaction(waiter);
                try
                {
                    Assert.True(TransactionWaitBudget.Remaining(timeout) < TimeSpan.FromMilliseconds(300));
                    Assert.Equal(120, Assert.Throws<LiteException>(() => locker.EnterLock("rows", waiter)).ErrorCode);
                    Assert.Equal(TimeSpan.Zero, TransactionWaitBudget.Remaining(timeout));
                }
                finally { locker.ExitTransaction(waiter); }
            }
            Assert.True(owner.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, locker.TransactionsCount);
            locker.ExitLock("rows", holder);
            locker.EnterTransaction(waiter);
            locker.EnterLock("rows", waiter);
            locker.ExitLock("rows", waiter);
            locker.ExitTransaction(waiter);
            Assert.Equal(0, locker.TransactionsCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Timed_out_handle_cleans_admission_snapshots_registry_and_queue(bool peer)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            using (var other = peer ? new LiteDatabase(file.Filename) : null)
            {
                db.Timeout = TimeSpan.FromSeconds(1);
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(TransactionHandleCooperativeWait_Tests.Row(9));
                using var holder = db.BeginTransaction();
                holder.GetCollection("rows").Insert(TransactionHandleCooperativeWait_Tests.Row(1));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var waiter = (other ?? db).BeginTransaction();
                    waiter.GetCollection("private").Insert(TransactionHandleCooperativeWait_Tests.Row(100 + attempt));
                    Assert.Equal(120, Assert.Throws<LiteException>(() => waiter.GetCollection("rows").Insert(TransactionHandleCooperativeWait_Tests.Row(2))).ErrorCode);
                    Assert.Equal(LiteTransactionState.Failed, waiter.State);
                    Assert.Single(engine.GetMonitor().Transactions);
                    Assert.Equal(1, TransactionHandleCooperativeWait_Tests.Locker(engine).TransactionsCount);
                    var collection = TransactionHandleCooperativeWait_Tests.Collection(engine, "rows");
                    var queue = (System.Collections.ICollection)typeof(CollectionLock).GetField("_waiters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(collection);
                    Assert.Equal(0, queue.Count);
                    Assert.Equal(1, db.TransactionHandles.ActiveCount + (other?.TransactionHandles.ActiveCount ?? 0));
                }
                holder.Commit();
                Assert.Empty(engine.GetMonitor().Transactions);
                Assert.Equal(0, TransactionHandleCooperativeWait_Tests.Locker(engine).TransactionsCount);
                using var successor = (other ?? db).BeginTransaction();
                successor.GetCollection("rows").Insert(TransactionHandleCooperativeWait_Tests.Row(3));
                successor.Commit();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(file);
                Assert.Equal(new[] { 1, 3 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
                Assert.Equal(0, db.GetCollection("private").Count());
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
                Assert.Equal(2, db.GetCollection("rows").Query().Where("_id >= 1").Count());
            }
        }

        [Fact]
        public void Begin_timeout_publishes_no_handle_or_admission_and_successor_can_begin()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.Timeout = TimeSpan.FromSeconds(1);
            var locker = TransactionHandleCooperativeWait_Tests.Locker(engine);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var maintenance = Task.Run(() =>
            {
                locker.EnterExclusive();
                held.Set();
                try { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
                finally { locker.ExitExclusive(); }
            });
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                Assert.Equal(120, Assert.Throws<LiteException>(() => db.BeginTransaction()).ErrorCode);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                Assert.Empty(engine.GetMonitor().Transactions);
                Assert.Equal(0, locker.TransactionsCount);
            }
            finally { release.Set(); Assert.True(maintenance.Wait(TimeSpan.FromSeconds(5))); }
            using var successor = db.BeginTransaction();
            successor.GetCollection("rows").Insert(TransactionHandleCooperativeWait_Tests.Row(1));
            successor.Commit();
            Assert.Empty(engine.GetMonitor().Transactions);
            Assert.Equal(0, locker.TransactionsCount);
        }

        [Fact]
        public void Fifo_waiters_progress_and_timeout_removes_only_its_request()
        {
            var collection = new CollectionLock();
            var holder = new object();
            Assert.True(collection.TryEnter(holder, TimeSpan.Zero));
            var order = new List<int>();
            var threads = new List<Thread>();
            var errors = new List<Exception>();
            for (var i = 0; i < 8; i++)
            {
                var id = i;
                using var waiting = new ManualResetEventSlim();
                Thread thread = null;
                collection.BeforeWait = () => { if (Thread.CurrentThread == thread) waiting.Set(); };
                thread = new Thread(() =>
                {
                    var error = Record.Exception(() =>
                {
                    var owner = new object();
                    Assert.True(collection.TryEnter(owner, TimeSpan.FromSeconds(10)));
                    lock (order) order.Add(id);
                    collection.Exit(owner);
                    });
                    if (error != null) lock (errors) errors.Add(error);
                });
                threads.Add(thread);
                thread.Start();
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
                // Synchronize with the callback under the lock before disposing its event.
                var probe = new object();
                collection.BeforeWait = null;
                Assert.False(collection.TryEnter(probe, TimeSpan.Zero));
            }
            collection.Exit(holder);
            foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            Assert.Empty(errors);
            Assert.Equal(Enumerable.Range(0, 8), order);
            Assert.True(collection.TryEnter(holder, TimeSpan.Zero));
            collection.Exit(holder);
        }
    }
}
