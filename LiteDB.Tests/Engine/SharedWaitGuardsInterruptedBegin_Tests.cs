using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// TransactionHandleInterruptedBegin_Tests on the wait-guard paths: a begin interrupted in the
    /// local handle queue or while its holder waits natively, with or without a SharedWriterTimeout,
    /// leaks no queue slot or writer ownership, and its recorded wait ends as neither a timeout
    /// nor a refusal.
    /// </summary>
    public class SharedWaitGuardsInterruptedBegin_Tests
    {
        public enum Stage { GateWait, HolderNativeWait }

#pragma warning disable CS0618
        [Theory]
        [InlineData(Stage.GateWait, false)] [InlineData(Stage.GateWait, true)]
        [InlineData(Stage.HolderNativeWait, false)] [InlineData(Stage.HolderNativeWait, true)]
        public void Interrupted_begin_in_the_local_queue_or_native_wait_leaks_nothing(Stage stage, bool bounded)
        {
            using var file = new TempFile();
            Seed(file);
            using (var owner = new LiteDatabase(new SharedEngine(Settings(file))))
            using (var engine = new SharedEngine(Settings(file, timeout: bounded ? TimeSpan.FromSeconds(10) : (TimeSpan?)null)))
            using (var db = new LiteDatabase(engine))
            {
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                // The begin waits in the local queue behind a handle, or natively behind a legacy owner.
                Exception holdingError = null;
                var holding = new Thread(() => holdingError = Record.Exception(() =>
                {
                    ILiteTransaction tx = null;
                    try
                    {
                        if (stage == Stage.HolderNativeWait) owner.BeginTrans(); else tx = owner.BeginTransaction();
                        (tx?.GetCollection("rows") ?? owner.GetCollection("rows")).Insert(Row(2));
                    }
                    finally { held.Set(); }
                    release.Wait(TimeSpan.FromSeconds(20));
                    if (tx == null) owner.Commit(); else tx.Commit();
                })) { IsBackground = true };
                holding.Start();
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                var before = db.GetSharedWaitDiagnostics().Total;
                Exception beginError = null;
                var begin = new Thread(() => beginError = Record.Exception(() => db.BeginTransaction().Dispose())) { IsBackground = true };
                begin.Start();
                // The begin's holder is queued at the file's turnstile, or the begin is queued locally.
                if (stage == Stage.HolderNativeWait)
                    Assert.True(SpinWait.SpinUntil(TransactionHandleSharedCallback_Tests.Turnstile(engine).HasWaiter, TimeSpan.FromSeconds(10)));
                else Assert.True(SharedHandleQueue.WaitForQueuedBegin(SharedHandleQueue.Of(db), TimeSpan.FromSeconds(10)));
                Assert.Equal(1, db.GetSharedWaitDiagnostics().CurrentWaiters);
                Assert.True(SpinWait.SpinUntil(() => (begin.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                begin.Interrupt();
                Assert.True(begin.Join(TimeSpan.FromSeconds(10)));
                Assert.IsType<ThreadInterruptedException>(beginError);
                // The wait ended at once and counts once: neither a timeout nor a refusal.
                var interrupted = db.GetSharedWaitDiagnostics();
                Assert.Equal(0, interrupted.CurrentWaiters);
                Assert.Equal((1, 0, 0), (interrupted.Total.Count - before.Count, interrupted.Total.TimedOut - before.TimedOut,
                    interrupted.Total.Refused - before.Refused));

                release.Set();
                Assert.True(holding.Join(TimeSpan.FromSeconds(10)));
                Assert.Null(holdingError);
                // Once the holder (if any) acquires and releases, other connections write and begin again.
                using var probe = new LiteDatabase(new SharedEngine(Settings(file)));
                var write = Task.Run(() => probe.GetCollection("rows").Insert(Row(3)));
                Assert.True(write.Wait(TimeSpan.FromSeconds(10)), "The interrupted begin's holder kept writer ownership.");
                var next = Task.Run(() => { using var tx = probe.BeginTransaction(); tx.GetCollection("rows").Insert(Row(4)); tx.Commit(); });
                Assert.True(next.Wait(TimeSpan.FromSeconds(10)), "The interrupted begin kept the local handle queue.");
                Assert.Equal(1, SharedHandleQueue.Of(db).CurrentCount);
                // The holder's own late admission added no second wait.
                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal((1, 0, 0), (after.Total.Count - before.Count, after.Total.TimedOut - before.TimedOut, after.CurrentWaiters));
            }
            Verify(file, null, 1, 2, 3, 4);
        }
#pragma warning restore CS0618
    }
}
