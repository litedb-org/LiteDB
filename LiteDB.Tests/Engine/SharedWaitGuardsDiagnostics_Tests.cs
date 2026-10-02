using System;
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
    }
}
