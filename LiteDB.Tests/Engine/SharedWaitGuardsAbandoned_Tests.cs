using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;
using static LiteDB.Tests.Engine.SharedWaitGuardsMutexes;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Under a finite SharedWriterTimeout an owner that ends without releasing (the OS abandons its
    /// mutex) is acquired from, and recovered, exactly as without a timeout: never a timeout.
    /// </summary>
    public class SharedWaitGuardsAbandoned_Tests
    {
        public enum Stage { Main, Turnstile }

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData(Stage.Main, null)] [InlineData(Stage.Main, "secret")]
        [InlineData(Stage.Turnstile, null)] [InlineData(Stage.Turnstile, "secret")]
        public void Abandoned_main_mutex_or_turnstile_under_a_finite_budget_is_acquired(Stage stage, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var engine = new SharedEngine(Settings(file, password, Budget)))
            using (var db = new LiteDatabase(engine))
            {
                db.GetCollection("rows").FindById(1);
                var before = db.GetSharedWaitDiagnostics().Total;
                using var queued = new ManualResetEventSlim();
                var turnstile = TransactionHandleSharedCallback_Tests.Turnstile(engine);
                // Queued at the turnstile, the write is about to wait for the native mutex.
                if (stage == Stage.Main) turnstile.BeforeMainWait = () => queued.Set();
                using var holder = new Holder(stage == Stage.Main ? MainMutex(engine) : TurnMutex(engine));
                Exception error = null;
                var elapsed = new Stopwatch();
                // The write acquires on its own thread (an array input), so its wait can be observed.
                var writer = new Thread(() =>
                {
                    elapsed.Start();
                    error = Record.Exception(() => engine.Insert("rows", new[] { Row(2) }, BsonAutoId.Int32));
                    elapsed.Stop();
                }) { IsBackground = true };
                try
                {
                    writer.Start();
                    if (stage == Stage.Main) Assert.True(queued.Wait(TimeSpan.FromSeconds(10)), "The write never queued for the native mutex.");
                    else Assert.True(SpinWait.SpinUntil(() => db.GetSharedWaitDiagnostics().CurrentWaiters == 1 &&
                        (writer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)),
                        "The write never waited for the turnstile.");
                }
                finally { turnstile.BeforeMainWait = null; }
                // The owner ends while the bounded write waits for it.
                holder.Abandon();
                Assert.True(writer.Join(Budget + TimeSpan.FromSeconds(10)), "The write did not complete.");
                Assert.Null(error);
                Assert.True(elapsed.Elapsed < Budget, $"Acquired after {elapsed.Elapsed}");
                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal(0, after.Total.TimedOut - before.TimedOut);
                Assert.Equal(1, after.Total.Count - before.Count);
                Assert.Equal(0, after.CurrentWaiters);
                // The abandoned mutex is owned by nobody now, and the connection keeps working.
                Assert.True(FreeOnAnotherThread(MainMutex(engine)) && FreeOnAnotherThread(TurnMutex(engine)), "A mutex stayed owned.");
                db.GetCollection("rows").Insert(Row(3));
            }
            // A Shared reopen, then two Direct reopens, see exactly the model.
            using (var shared = new LiteDatabase(new SharedEngine(Settings(file, password))))
                Assert.Equal(new[] { 1, 2, 3 }, shared.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id));
            Verify(file, password, 1, 2, 3);
        }
    }
}
