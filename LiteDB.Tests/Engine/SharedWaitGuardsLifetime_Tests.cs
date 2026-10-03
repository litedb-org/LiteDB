using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>Ownership lifetime and refusal precision of the Shared wait guards (#3080 review).</summary>
    public class SharedWaitGuardsLifetime_Tests
    {
        private sealed class ObserverSession
        {
            internal LiteDatabase Db;
            internal ILiteTransaction Tx;
            internal int Reports;
            internal void Observe(SharedSlowWait wait) => Reports++;
        }

        [Fact]
        public void An_observer_capturing_the_handle_does_not_keep_an_abandoned_handle_owning_the_mutex()
        {
            using var file = new TempFile();
            Seed(file);
            var session = Abandon(file);
            for (var attempt = 0; attempt < 100 && session.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }
            if (session.Target is ObserverSession leaked)
            {
                // Bounded escape: release the holder, then fail.
                leaked.Tx.Rollback();
                Assert.Fail("The holder's wait recorder kept the abandoned session and its handle alive.");
            }
            // The abandoned handle released native writer ownership: a fresh connection writes.
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, timeout: TimeSpan.FromSeconds(20)))))
                db.GetCollection("rows").Insert(Row(3));
            Verify(file, null, 1, 3);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(string file)
        {
            var session = new ObserverSession();
            var settings = Settings(file);
            // Retaining the observer is enough to matter; it never needs to run.
            settings.SharedSlowWait = session.Observe;
            session.Db = new LiteDatabase(new SharedEngine(settings));
            session.Tx = Unmarked(() => session.Db.BeginTransaction()).Result;
            Unmarked(() => session.Tx.GetCollection("rows").Insert(Row(2))).Wait();
            return new WeakReference(session);
        }

        [Fact]
        public void Grace_keeps_waiting_while_one_long_handle_operation_executes()
        {
            using var file = new TempFile();
            Seed(file);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var armed = 0;
            var settings = Settings(file, timeout: TimeSpan.FromSeconds(30), grace: TimeSpan.FromMilliseconds(300));
            settings.ReadTransform = (_, value) =>
            {
                if (value.IsDocument && value["_id"] == 1 && Volatile.Read(ref armed) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                }
                return value;
            };
            using (var db = new LiteDatabase(new SharedEngine(settings)))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                Volatile.Write(ref armed, 1);
                // One handle operation executes far longer than the grace, then commits.
                var operation = Task.Run(() =>
                {
                    Assert.NotNull(tx.GetCollection("rows").FindById(1));
                    tx.Commit();
                });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                var releaser = Unmarked(async () =>
                {
                    await Task.Delay(800);
                    Volatile.Write(ref armed, 0);
                    release.Set();
                });
                var elapsed = Stopwatch.StartNew();
                db.GetCollection("rows").Insert(Row(3));
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500), $"Did not wait for the executing operation: {elapsed.Elapsed}");
                Assert.True(operation.Wait(TimeSpan.FromSeconds(20)));
                releaser.Wait(TimeSpan.FromSeconds(20));
                Assert.Equal(0, db.GetSharedWaitDiagnostics().Total.Refused);
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Self_wait_guard_covers_every_database_the_flow_holds_a_handle_for()
        {
            using var a = new TempFile();
            using var b = new TempFile();
            Seed(a);
            Seed(b);
            // A finite timeout turns a missed refusal into LOCK_TIMEOUT instead of a hang.
            var bounded = TimeSpan.FromSeconds(5);
            using (var dbA = new LiteDatabase(new SharedEngine(Settings(a, timeout: bounded, grace: Immediately))))
            using (var dbB = new LiteDatabase(new SharedEngine(Settings(b, timeout: bounded, grace: Immediately))))
            {
                var txA = dbA.BeginTransaction();
                txA.GetCollection("rows").Insert(Row(2));
                using (var txB = dbB.BeginTransaction())
                {
                    txB.GetCollection("rows").Insert(Row(3));
                    // Using B must not erase the marker for A, and vice versa.
                    Assert.Throws<InvalidOperationException>(() => dbA.GetCollection("rows").Insert(Row(90)));
                    Assert.Throws<InvalidOperationException>(() => dbB.GetCollection("rows").Insert(Row(91)));
                    txB.Rollback();
                }
                // A completed B leaves A's marker in place; B is free again.
                Assert.Throws<InvalidOperationException>(() => dbA.GetCollection("rows").Insert(Row(92)));
                Assert.IsType<InvalidOperationException>(Task.Run(() => Record.Exception(() => dbA.GetCollection("rows").Insert(Row(93)))).Result);
                dbB.GetCollection("rows").Insert(Row(5));
                Assert.Equal(LiteTransactionState.Active, txA.State);
                txA.GetCollection("rows").Insert(Row(4));
                txA.Commit();
                dbA.GetCollection("rows").Insert(Row(6));
            }
            Verify(a, null, 1, 2, 4, 6);
            Verify(b, null, 1, 5);
        }

        [Fact]
        public void Owner_held_and_idle_times_start_when_ownership_is_acquired()
        {
            using var file = new TempFile();
            Seed(file);
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            using (var db = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                using var held = new ManualResetEventSlim();
                var legacy = Unmarked(() =>
                {
#pragma warning disable CS0618
                    other.BeginTrans();
                    other.GetCollection("rows").Insert(Row(2));
                    held.Set();
                    Thread.Sleep(1500);
                    other.Commit();
#pragma warning restore CS0618
                });
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                var waited = Stopwatch.StartNew();
                using var tx = db.BeginTransaction();
                waited.Stop();
                var diagnostics = db.GetSharedWaitDiagnostics();
                Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(1000), $"Begin did not wait: {waited.Elapsed}");
                Assert.Equal(SharedWriterOwner.TransactionHandle, diagnostics.Owner);
                Assert.True(diagnostics.OwnerHeld < TimeSpan.FromMilliseconds(800), $"Held includes the wait: {diagnostics.OwnerHeld}");
                Assert.True(diagnostics.OwnerIdle < TimeSpan.FromMilliseconds(800), $"Idle includes the wait: {diagnostics.OwnerIdle}");
                Assert.True(diagnostics.Total.MaxWait >= TimeSpan.FromMilliseconds(1000));
                tx.GetCollection("rows").Insert(Row(3));
                tx.Commit();
                Assert.True(legacy.Wait(TimeSpan.FromSeconds(20)));
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Connection_string_round_trip_preserves_the_wait_options()
        {
            var configured = new ConnectionString("filename=a.db;shared writer timeout=00:00:01.5;shared self wait grace=0");
            var copy = new ConnectionString(configured.ToString());
            Assert.Equal(TimeSpan.FromMilliseconds(1500), copy.SharedWriterTimeout);
            Assert.Equal(TimeSpan.Zero, copy.SharedSelfWaitGrace);
            var defaults = new ConnectionString("filename=a.db");
            Assert.DoesNotContain("shared", defaults.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, new ConnectionString(defaults.ToString()).SharedWriterTimeout);
        }
    }
}
