using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>What Shared wait diagnostics count: one entry per wait, and refusals only as refusals.</summary>
    public class SharedWaitGuardsDiagnostics_Tests
    {
        public enum Accounting { Immediate, Timeout, ZeroGraceRefusal, PostGraceRefusal }

        [Theory]
        [InlineData(Accounting.Immediate, 1, 0, 0, null)]
        [InlineData(Accounting.Timeout, 1, 1, 0, true)]
        [InlineData(Accounting.ZeroGraceRefusal, 0, 0, 1, false)]
        [InlineData(Accounting.PostGraceRefusal, 0, 0, 1, false)]
        public void Count_and_refusal_accounting(Accounting scenario, long count, long timedOut, long refused, bool? waited)
        {
            using var file = new TempFile();
            Seed(file);
            var grace = scenario == Accounting.ZeroGraceRefusal ? TimeSpan.Zero
                : scenario == Accounting.PostGraceRefusal ? TimeSpan.FromMilliseconds(100) : (TimeSpan?)null;
            var timeout = scenario == Accounting.Timeout ? TimeSpan.FromMilliseconds(100) : (TimeSpan?)null;
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, timeout: timeout, grace: grace))))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                db.GetCollection("rows").FindById(1);
                using var release = new ManualResetEventSlim();
                using var held = new ManualResetEventSlim();
                Task legacy = Task.CompletedTask;
                ILiteTransaction tx = null;
                if (scenario == Accounting.Timeout)
                {
                    legacy = Unmarked(() =>
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
                }
                // This flow holds the owning handle: its child task's write is a self-wait.
                if (grace != null) tx = db.BeginTransaction();
                var before = db.GetSharedWaitDiagnostics().Total;

                var write = Task.Run(() => Record.Exception(() => db.GetCollection("rows").Insert(Row(3))));
                if (tx != null) AssertRefusedWithin(write, tx);
                Assert.True(write.Wait(TimeSpan.FromSeconds(20)));
                var error = write.Result;
                if (scenario == Accounting.Immediate) Assert.Null(error);
                else if (scenario == Accounting.Timeout) Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(error).ErrorCode);
                else Assert.IsType<InvalidOperationException>(error);

                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal((count, timedOut, refused), (after.Total.Count - before.Count, after.Total.TimedOut - before.TimedOut,
                    after.Total.Refused - before.Refused));
                // The recent window agrees with the totals.
                Assert.Equal((count, timedOut, refused), (after.Recent.Count - before.Count, after.Recent.TimedOut - before.TimedOut,
                    after.Recent.Refused - before.Refused));
                if (waited != null) Assert.Equal(waited.Value, after.Total.TotalWait > before.TotalWait);
                Assert.Equal(0, after.CurrentWaiters);

                tx?.Commit();
                release.Set();
                Assert.True(legacy.Wait(TimeSpan.FromSeconds(20)));
            }
            Verify(file, null, scenario == Accounting.Immediate ? new[] { 1, 3 } : scenario == Accounting.Timeout ? new[] { 1, 2 } : new[] { 1 });
        }

        [Fact]
        public void One_begin_records_one_wait_across_queue_and_native_stages()
        {
            using var file = new TempFile();
            Seed(file);
            var reports = new ConcurrentQueue<SharedSlowWait>();
            using var reported = new ManualResetEventSlim();
            var settings = Settings(file);
            settings.SharedSlowWaitThreshold = TimeSpan.FromMilliseconds(500);
            settings.SharedSlowWait = info => { reports.Enqueue(info); reported.Set(); };
            using (var db = new LiteDatabase(new SharedEngine(settings)))
            using (var peerEngine = new SharedEngine(Settings(file)))
            using (var peer = new LiteDatabase(peerEngine))
            {
                // Uncontended: one begin, one wait.
                var before = db.GetSharedWaitDiagnostics().Total.Count;
                Unmarked(() => { using var tx = db.BeginTransaction(); tx.Commit(); }).Wait();
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.Count - before);

                // The first handle holds this process's handle queue and the native mutex.
                var first = Unmarked(() => { var tx = db.BeginTransaction(); tx.GetCollection("rows").Insert(Row(2)); return tx; }).Result;
                before = db.GetSharedWaitDiagnostics().Total.Count;
                var begin = Unmarked(() =>
                {
                    using var tx = db.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(4));
                    tx.Commit();
                });
                Assert.True(SpinWait.SpinUntil(() => db.GetSharedWaitDiagnostics().CurrentWaiters == 1, TimeSpan.FromSeconds(10)));

                // A legacy writer of another connection queues at the turnstile for the native
                // mutex: once the first handle ends it goes first, so the begin waits for it natively.
                using var peerQueued = new ManualResetEventSlim();
                using var peerOwns = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                TransactionHandleSharedCallback_Tests.Turnstile(peerEngine).BeforeMainWait = () => peerQueued.Set();
                var legacy = Unmarked(() =>
                {
#pragma warning disable CS0618
                    peer.BeginTrans();
                    peer.GetCollection("rows").Insert(Row(3));
                    peerOwns.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    peer.Commit();
#pragma warning restore CS0618
                });
                Assert.True(peerQueued.Wait(TimeSpan.FromSeconds(10)));
                TransactionHandleSharedCallback_Tests.Turnstile(peerEngine).BeforeMainWait = null;

                // Stage one: about 400 ms in the local handle queue.
                Assert.True(SpinWait.SpinUntil(() => db.GetSharedWaitDiagnostics().LongestCurrentWait >= TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromSeconds(10)));
                Unmarked(first.Commit).Wait();
                Assert.True(peerOwns.Wait(TimeSpan.FromSeconds(10)));
                // Stage two: about 400 ms more for native admission behind the legacy writer.
                Assert.True(SpinWait.SpinUntil(() => db.GetSharedWaitDiagnostics().LongestCurrentWait >= TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromSeconds(10)));
                Assert.False(begin.IsCompleted);
                var during = db.GetSharedWaitDiagnostics();
                Assert.Equal(1, during.CurrentWaiters);
                release.Set();
                Assert.True(legacy.Wait(TimeSpan.FromSeconds(20)));
                Assert.True(begin.Wait(TimeSpan.FromSeconds(20)));

                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal(1, after.Total.Count - before);
                Assert.Equal(0, after.CurrentWaiters);
                Assert.True(after.Total.MaxWait >= TimeSpan.FromMilliseconds(800), $"MaxWait {after.Total.MaxWait}");
                Assert.True(reported.Wait(TimeSpan.FromSeconds(10)), "The combined wait was never reported.");
                var report = Assert.Single(reports);
                Assert.True(report.Elapsed >= TimeSpan.FromMilliseconds(800), $"Reported {report.Elapsed}");
                Assert.False(report.TimedOut);
            }
            Verify(file, null, 1, 2, 3, 4);
        }

        [Fact]
        public void Pin_wait_excludes_engine_open()
        {
            using var file = new TempFile();
            Seed(file);
            var reports = new ConcurrentQueue<SharedSlowWait>();
            var settings = Settings(file);
            settings.SharedSlowWaitThreshold = TimeSpan.FromMilliseconds(200);
            settings.SharedSlowWait = reports.Enqueue;
            // A read callback keeps queries off mapped snapshots, so the query below is a leased reader.
            settings.ReadTransform = (_, value) => value;
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine))
            {
                // A thread streaming a leased reader writes through a pin, which opens the engine.
                using var anchor = engine.Query("sentinel", new Query());
                Assert.True(anchor.Read());
                var applied = (EngineSettings)typeof(SharedEngine).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                var opens = 0;
                // A slow open (or recovery), with the mutex itself uncontended.
                engine.SimulateOpenEngine = () =>
                {
                    opens++;
                    Thread.Sleep(300);
                    return new LiteEngine(applied);
                };
                var before = db.GetSharedWaitDiagnostics().Total;
                try { db.GetCollection("rows").Insert(Row(3)); }
                finally { engine.SimulateOpenEngine = null; }
                Assert.Equal(1, opens);
                Assert.NotNull(engine.Pin);
                anchor.Dispose();
                var after = db.GetSharedWaitDiagnostics().Total;
                Assert.True(after.Count > before.Count, "The pin's acquisition was not recorded.");
                Assert.True(after.MaxWait < TimeSpan.FromMilliseconds(100), $"The recorded wait includes the engine open: {after.MaxWait}");
                // No wait reached the 200 ms threshold, so nothing was queued for the observer.
                Assert.Empty(reports);
            }
            Verify(file, null, 1, 3);
        }

        [Fact]
        public void Recent_window_covers_waits_just_before_a_minute_boundary()
        {
            using var file = new TempFile();
            Seed(file);
            using (var engine = new SharedEngine(Settings(file)))
            using (var db = new LiteDatabase(engine))
            {
                db.GetCollection("rows").FindById(1);
                // Move the recorder's epoch instead of waiting for a real minute boundary.
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var recorder = typeof(SharedEngine).GetField("_waitRecorder", flags).GetValue(engine);
                var created = recorder.GetType().GetField("_created", flags);
                var second = Stopwatch.Frequency;
                // Now is 58 s into a minute: the next wait falls shortly before a minute boundary.
                created.SetValue(recorder, Stopwatch.GetTimestamp() - 58 * second);
                db.GetCollection("rows").Insert(Row(2));
                // Four seconds later, just past the boundary.
                created.SetValue(recorder, (long)created.GetValue(recorder) - 4 * second);
                var diagnostics = db.GetSharedWaitDiagnostics(TimeSpan.FromMinutes(1));
                Assert.True(diagnostics.Total.Count >= 2);
                Assert.Equal(diagnostics.Total.Count, diagnostics.Recent.Count);
                Assert.InRange(diagnostics.Window, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            }
            Verify(file, null, 1, 2);
        }
    }
}
