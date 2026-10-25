using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>Shared-mode self-wait refusal, SharedWriterTimeout and wait diagnostics (#3080).</summary>
    public class SharedWaitGuards_Tests
    {
        internal static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        internal static EngineSettings Settings(string file, string password = null, TimeSpan? timeout = null, TimeSpan? grace = null) =>
            new EngineSettings { Filename = file, Password = password, SharedWriterTimeout = timeout ?? Timeout.InfiniteTimeSpan,
                SharedSelfWaitGrace = grace ?? Timeout.InfiniteTimeSpan };

        internal static readonly TimeSpan Immediately = TimeSpan.Zero;

        internal static void Seed(string file, string password = null)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(Row(1));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(9));
        }

        internal static void Verify(string file, string password, params int[] ids)
        {
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                var query = db.GetCollection("rows").Query().Where(Query.GTE("value", 0));
                Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                Assert.Equal(ids, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }

        // The child task inherits nothing from the test thread's flow, and marks nothing on it.
        internal static Task<T> Unmarked<T>(Func<T> action)
        {
            // A dedicated thread started without flow: a pool task could be inlined into the
            // caller's flow by Wait/Result (it runs there if not started yet), marking that flow.
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { result.SetResult(action()); }
                catch (Exception error) { result.SetException(error); }
            }) { IsBackground = true };
            var flow = ExecutionContext.SuppressFlow();
            try { thread.Start(); }
            finally { flow.Undo(); }
            return result.Task;
        }

        internal static Task Unmarked(Action action) => Unmarked(() => { action(); return true; });

        // A regressed refusal would wait for the handle; complete it so the host is not wedged.
        internal static void AssertRefusedWithin(Task call, ILiteTransaction holder)
        {
            if (!call.Wait(TimeSpan.FromSeconds(10)))
            {
                holder.Rollback();
                Assert.Fail("The self-dependent call waited instead of being refused.");
            }
        }

        [Theory]
        [InlineData(false, false, null)] [InlineData(true, false, null)]
        [InlineData(false, true, null)] [InlineData(true, true, "secret")]
        public async Task Ordinary_call_from_the_flow_holding_an_idle_handle_is_refused(bool afterAwait, bool peerConnection, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, password, grace: Immediately))))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password, grace: Immediately))))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                if (afterAwait) await Task.Delay(10);
                var target = peerConnection ? peer : db;
                // A child task inherits the flow, so it cannot complete the handle either.
                var write = Task.Run(() => Record.Exception(() => target.GetCollection("rows").Insert(Row(3))));
                AssertRefusedWithin(write, tx);
                Assert.IsType<InvalidOperationException>(await write);
                Assert.IsType<InvalidOperationException>(Record.Exception(() => target.UserVersion = 7));
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.Equal(2, target.GetSharedWaitDiagnostics().Total.Refused);
                // Refused calls never waited; only the handle's own admission was recorded.
                Assert.Equal(peerConnection ? 0 : 1, target.GetSharedWaitDiagnostics().Total.Count);
                tx.GetCollection("rows").Insert(Row(4));
                tx.Commit();
                // A completed handle no longer marks its flow.
                target.GetCollection("rows").Insert(Row(5));
            }
            Verify(file, password, 1, 2, 4, 5);
        }

        [Fact]
        public void Second_handle_from_the_same_flow_is_refused_and_another_flow_waits_for_it()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, grace: Immediately))))
            {
                var first = db.BeginTransaction();
                first.GetCollection("rows").Insert(Row(2));
                var nested = Task.Run(() => Record.Exception(() => db.BeginTransaction().Dispose()));
                AssertRefusedWithin(nested, first);
                Assert.IsType<InvalidOperationException>(nested.Result);

                var other = Unmarked(() =>
                {
                    using var second = db.BeginTransaction();
                    second.GetCollection("rows").Insert(Row(3));
                    second.Commit();
                });
                Assert.False(other.Wait(300));
                first.Commit();
                Assert.True(other.Wait(TimeSpan.FromSeconds(20)));
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void By_default_the_holding_flow_waits_for_a_handle_completed_elsewhere()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                // The completing task inherits this flow, as an ordinary handoff does.
                var completer = Task.Run(async () => { await Task.Delay(300); tx.Commit(); });
                db.GetCollection("rows").Insert(Row(3));
                Assert.True(completer.Wait(TimeSpan.FromSeconds(20)));
                Assert.Equal(0, db.GetSharedWaitDiagnostics().Total.Refused);
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Grace_refuses_only_after_the_owning_handle_stayed_idle()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, grace: TimeSpan.FromMilliseconds(300)))))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var elapsed = Stopwatch.StartNew();
                var write = Task.Run(() => Record.Exception(() => db.GetCollection("rows").Insert(Row(3))));
                AssertRefusedWithin(write, tx);
                Assert.IsType<InvalidOperationException>(write.Result);
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(250), $"Refused after {elapsed.Elapsed}");
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.Refused);
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Commit();
            }
            Verify(file, null, 1, 2);
        }

        [Fact]
        public void Grace_keeps_waiting_while_another_thread_still_uses_the_handle()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, grace: TimeSpan.FromMilliseconds(300)))))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var completer = Task.Run(async () =>
                {
                    for (var i = 0; i < 10; i++)
                    {
                        await Task.Delay(100);
                        tx.GetCollection("rows").FindById(2);
                    }
                    tx.Commit();
                });
                var elapsed = Stopwatch.StartNew();
                db.GetCollection("rows").Insert(Row(3));
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500), $"Did not wait for the active handle: {elapsed.Elapsed}");
                Assert.True(completer.Wait(TimeSpan.FromSeconds(20)));
                Assert.Equal(0, db.GetSharedWaitDiagnostics().Total.Refused);
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Other_flows_wait_for_the_handle_and_diagnostics_name_it()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var waiter = Unmarked(() => db.GetCollection("rows").Insert(Row(3)));
                var started = Stopwatch.StartNew();
                SharedWaitDiagnostics during;
                do
                {
                    during = db.GetSharedWaitDiagnostics();
                    Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "The other flow never waited.");
                } while (during.CurrentWaiters == 0);
                Assert.Equal(SharedWriterOwner.TransactionHandle, during.Owner);
                Thread.Sleep(600);
                during = db.GetSharedWaitDiagnostics();
                Assert.True(during.LongestCurrentWait >= TimeSpan.FromMilliseconds(500));
                Assert.True(during.OwnerHeld >= during.OwnerIdle);
                Assert.True(during.OwnerIdle >= TimeSpan.FromMilliseconds(500));
                Assert.False(waiter.IsCompleted);
                Unmarked(tx.Commit).Wait();
                Assert.True(waiter.Wait(TimeSpan.FromSeconds(20)));
                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal(0, after.CurrentWaiters);
                Assert.Equal(SharedWriterOwner.Unknown, after.Owner);
                Assert.True(after.Recent.Over500Milliseconds >= 1);
                Assert.True(after.Recent.MaxWait >= TimeSpan.FromMilliseconds(500));
                Assert.Equal(0, after.Total.TimedOut);
                // The current partial minute plus five whole minutes.
                Assert.InRange(after.Window, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Direct_mode_has_no_self_wait_to_refuse()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(file))
            {
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                db.GetCollection("sentinel").Insert(Row(8));
                Assert.Null(db.GetSharedWaitDiagnostics());
                tx.Commit();
            }
            Verify(file, null, 1, 2);
        }

        public enum WaitPath { Scoped, Holder, Pin }

        [Theory]
        [InlineData(WaitPath.Scoped, false, null)] [InlineData(WaitPath.Scoped, true, "secret")]
        [InlineData(WaitPath.Holder, false, null)] [InlineData(WaitPath.Holder, true, null)]
        [InlineData(WaitPath.Pin, false, null)] [InlineData(WaitPath.Pin, true, "secret")]
        public void Writer_wait_times_out_without_side_effects_and_later_writes_succeed(WaitPath path, bool handleOwner, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            var settings = Settings(file, password, TimeSpan.FromMilliseconds(300));
            // A read callback makes ordinary calls acquire through the connection's holder thread.
            if (path != WaitPath.Scoped) settings.ReadTransform = (_, value) => value;
            using (var owner = new LiteDatabase(new SharedEngine(Settings(file, password))))
            using (var waiterEngine = new SharedEngine(settings))
            using (var waiter = new LiteDatabase(waiterEngine))
            {
                IBsonDataReader anchor = null;
                if (path == WaitPath.Pin)
                {
                    // A thread streaming a leased reader writes through a pin. The lease
                    // itself is taken before the owner acquires writer ownership.
                    anchor = waiterEngine.Query("sentinel", new Query());
                    Assert.True(anchor.Read());
                }
                ILiteTransaction tx = null;
                using var release = new ManualResetEventSlim();
                Task holder;
                if (handleOwner)
                {
                    tx = Unmarked(() => { var t = owner.BeginTransaction(); t.GetCollection("rows").Insert(Row(2)); return t; }).Result;
                    holder = Task.CompletedTask;
                }
                else
                {
                    using var held = new ManualResetEventSlim();
                    holder = Unmarked(() =>
                    {
#pragma warning disable CS0618
                        owner.BeginTrans();
                        owner.GetCollection("rows").Insert(Row(2));
                        held.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                        owner.Commit();
#pragma warning restore CS0618
                    });
                    Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                }
                var elapsed = Stopwatch.StartNew();
                var timeout = Assert.Throws<LiteException>(() => waiter.GetCollection("rows").Insert(Row(3)));
                elapsed.Stop();
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.Contains("SharedWriterTimeout", timeout.Message);
                if (handleOwner) Assert.Contains("transaction handle", timeout.Message);
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(250), $"Timed out after {elapsed.Elapsed}");
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Timed out after {elapsed.Elapsed}");
                anchor?.Dispose();
                var diagnostics = waiter.GetSharedWaitDiagnostics();
                Assert.Equal(1, diagnostics.Total.TimedOut);
                Assert.Equal(0, diagnostics.CurrentWaiters);

                if (handleOwner) Unmarked(tx.Commit).Wait();
                else release.Set();
                Assert.True(holder.Wait(TimeSpan.FromSeconds(20)));
                // The timed-out wait left no turnstile, mutex or gate ownership behind.
                waiter.GetCollection("rows").Insert(Row(4));
                owner.GetCollection("rows").Insert(Row(5));
            }
            Verify(file, password, 1, 2, 4, 5);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Begin_spends_one_timeout_on_the_local_queue_or_native_admission(bool localQueue)
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, timeout: TimeSpan.FromMilliseconds(300)))))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                // Same connection: the next handle queues locally. Another connection's legacy
                // transaction: the next handle's holder waits for native admission.
                var owner = localQueue ? db : other;
                using var release = new ManualResetEventSlim();
                using var held = new ManualResetEventSlim();
                var holder = Unmarked(() =>
                {
                    if (localQueue)
                    {
                        using var first = owner.BeginTransaction();
                        first.GetCollection("rows").Insert(Row(2));
                        held.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                        first.Commit();
                    }
                    else
                    {
#pragma warning disable CS0618
                        owner.BeginTrans();
                        owner.GetCollection("rows").Insert(Row(2));
                        held.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                        owner.Commit();
#pragma warning restore CS0618
                    }
                });
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                var elapsed = Stopwatch.StartNew();
                var timeout = Assert.Throws<LiteException>(() => db.BeginTransaction());
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Timed out after {elapsed.Elapsed}");
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.TimedOut);
                Assert.Equal(localQueue ? 1 : 0, db.TransactionHandles.ActiveCount);
                release.Set();
                Assert.True(holder.Wait(TimeSpan.FromSeconds(20)));
                using (var next = db.BeginTransaction())
                {
                    next.GetCollection("rows").Insert(Row(3));
                    next.Commit();
                }
            }
            Verify(file, null, 1, 2, 3);
        }

        [Fact]
        public void Slow_waits_are_reported_off_the_waiting_thread_and_observer_failures_are_ignored()
        {
            using var file = new TempFile();
            Seed(file);
            using var reported = new ManualResetEventSlim();
            SharedSlowWait report = null;
            var settings = Settings(file);
            settings.SharedSlowWaitThreshold = TimeSpan.FromMilliseconds(100);
            var waitingThread = 0;
            var observerThread = 0;
            settings.SharedSlowWait = info =>
            {
                observerThread = Environment.CurrentManagedThreadId;
                report = info;
                reported.Set();
                throw new InvalidOperationException("observer failure must not escape");
            };
            using (var db = new LiteDatabase(new SharedEngine(settings)))
            {
                var tx = Unmarked(() => { var t = db.BeginTransaction(); t.GetCollection("rows").Insert(Row(2)); return t; }).Result;
                var commit = Task.Run(async () => { await Task.Delay(400); tx.Commit(); });
                waitingThread = Environment.CurrentManagedThreadId;
                db.GetCollection("rows").Insert(Row(3));
                Assert.True(commit.Wait(TimeSpan.FromSeconds(20)));
                Assert.True(reported.Wait(TimeSpan.FromSeconds(10)));
                Assert.NotEqual(waitingThread, observerThread);
                Assert.True(report.Elapsed >= TimeSpan.FromMilliseconds(100));
                Assert.False(report.TimedOut);
                Assert.Equal(SharedWriterOwner.TransactionHandle, report.Owner);
                Assert.Equal(file.Filename, report.Filename);
                db.GetCollection("rows").Insert(Row(4));
            }
            Verify(file, null, 1, 2, 3, 4);
        }

        [Fact]
        public void Timeout_settings_validate_and_parse_from_connection_strings()
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, new EngineSettings().SharedWriterTimeout);
            Assert.Equal(Timeout.InfiniteTimeSpan, new ConnectionString("filename=a.db").SharedWriterTimeout);
            Assert.Equal(TimeSpan.FromSeconds(30), new ConnectionString("filename=a.db;shared writer timeout=30").SharedWriterTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), new ConnectionString("filename=a.db;shared writer timeout=00:00:01.5").SharedWriterTimeout);
            Assert.Equal(Timeout.InfiniteTimeSpan, new ConnectionString("filename=a.db;shared writer timeout=infinite").SharedWriterTimeout);
            Assert.Equal(Timeout.InfiniteTimeSpan, new ConnectionString("filename=a.db;shared writer timeout=-1").SharedWriterTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), new ConnectionString("shared writer timeout=5;filename=a.db").SharedWriterTimeout);
            Assert.Throws<LiteException>(() => new ConnectionString("filename=a.db;shared writer timeout=soon"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EngineSettings { SharedWriterTimeout = TimeSpan.FromMilliseconds(-5) });
            Assert.Throws<ArgumentOutOfRangeException>(() => new EngineSettings { SharedWriterTimeout = TimeSpan.FromDays(30) });
            Assert.Equal(Timeout.InfiniteTimeSpan, new EngineSettings().SharedSelfWaitGrace);
            Assert.Equal(TimeSpan.Zero, new ConnectionString("filename=a.db;shared self wait grace=0").SharedSelfWaitGrace);
            Assert.Equal(TimeSpan.FromSeconds(2), new ConnectionString("shared self wait grace=00:00:02;filename=a.db").SharedSelfWaitGrace);
            Assert.Throws<ArgumentOutOfRangeException>(() => new EngineSettings { SharedSelfWaitGrace = TimeSpan.FromMilliseconds(-5) });

            using var file = new TempFile();
            Seed(file);
            var cs = new ConnectionString { Filename = file, Connection = ConnectionType.Shared, SharedWriterTimeout = TimeSpan.FromSeconds(7),
                SharedSelfWaitGrace = TimeSpan.FromSeconds(3) };
            using var db = new LiteDatabase(cs);
            var engine = (SharedEngine)typeof(LiteDatabase).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(db);
            var applied = (EngineSettings)typeof(SharedEngine).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            Assert.Equal(TimeSpan.FromSeconds(7), applied.SharedWriterTimeout);
            Assert.Equal(TimeSpan.FromSeconds(3), applied.SharedSelfWaitGrace);
        }
    }
}
