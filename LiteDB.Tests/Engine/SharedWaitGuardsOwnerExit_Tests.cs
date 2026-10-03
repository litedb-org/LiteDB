using System;
using System.Diagnostics;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A bounded Shared writer wait never runs an exited owner's cleanup on the waiting thread:
    /// slow cleanup (it can close engine resources) does not extend the wait past its budget.
    /// </summary>
    public class SharedWaitGuardsOwnerExit_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };

#pragma warning disable CS0618
        [Fact]
        public void Bounded_wait_behind_an_exited_owner_returns_within_its_budget()
        {
            // Who claims the exited owner first (the holder's poll or the waiter) is a race; with
            // the waiter claiming it, the old code waited for the whole cleanup. Several rounds
            // make that outcome all but certain to show up.
            for (var round = 0; round < 8; round++)
            {
                using var file = new TempFile();
                using var cleaning = new ManualResetEventSlim();
                using var proceed = new ManualResetEventSlim();
                var settings = new EngineSettings { Filename = file, SharedWriterTimeout = TimeSpan.FromSeconds(1) };
                using var engine = new SharedEngine(settings);
                using var database = new LiteDatabase(engine);
                database.GetCollection("rows").Insert(Row(0));
                engine.MutexOwner.BeforeOwnerExitedCleanup = () => { cleaning.Set(); proceed.Wait(TimeSpan.FromSeconds(10)); };
                Exception ownerError = null;
                using var owning = new ManualResetEventSlim();
                using var exit = new ManualResetEventSlim();
                var owner = new Thread(() =>
                {
                    ownerError = Record.Exception(() =>
                    {
                        database.BeginTrans();
                        database.GetCollection("rows").Insert(Row(1));
                    });
                    owning.Set();
                    exit.Wait(TimeSpan.FromSeconds(10));
                });
                owner.Start();
                Assert.True(owning.Wait(TimeSpan.FromSeconds(10)));
                Assert.Null(ownerError);
                // The waiter queues behind the live owner; the owner thread then exits, and the
                // waiter's poll and the holder's poll race to claim the exited owner.
                Exception waitError = null;
                var waiter = new Thread(() => waitError = Record.Exception(() => database.GetCollection("rows").Insert(Row(2))));
                waiter.Start();
                Assert.True(SpinWait.SpinUntil(() => (waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                exit.Set();
                Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
                try
                {
                    Assert.True(cleaning.Wait(TimeSpan.FromSeconds(10)), "The exited owner was never cleaned up.");
                    Assert.True(waiter.Join(TimeSpan.FromSeconds(3)), $"Round {round}: the bounded wait waited for the exited owner's cleanup.");
                    Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(waitError).ErrorCode);
                }
                finally
                {
                    proceed.Set();
                    waiter.Join(TimeSpan.FromSeconds(10));
                    engine.MutexOwner.BeforeOwnerExitedCleanup = null;
                }
                // After the cleanup the next call reports the abandoned transaction once, as
                // without a timeout; then the connection writes again and the owner's insert is gone.
                var reported = Record.Exception(() => database.GetCollection("rows").Insert(Row(3)));
                if (reported != null) Assert.Contains("owner thread exited", reported.Message);
                if (reported != null) database.GetCollection("rows").Insert(Row(3));
                Assert.Null(database.GetCollection("rows").FindById(1));
            }
        }
#pragma warning restore CS0618
    }
}
