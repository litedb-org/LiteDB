using System;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A reused holder wrapper never reports the previous handle's native admission: a later
    /// begin that times out natively records its whole wait.
    /// </summary>
    public class TransactionHandleReuseWaits_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };

#pragma warning disable CS0618
        [Fact]
        public void Begin_on_a_reused_wrapper_that_times_out_natively_records_its_whole_wait()
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Connection = ConnectionType.Shared };
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared, SharedWriterTimeout = TimeSpan.FromMilliseconds(400) });
            // The first handle is admitted at once and leaves its wrapper cached.
            using (var tx = db.BeginTransaction()) { tx.GetCollection("rows").Insert(Row(1)); tx.Commit(); }
            using var other = new LiteDatabase(settings);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Exception ownerError = null;
            var owner = new Thread(() => ownerError = Record.Exception(() =>
            {
                other.BeginTrans();
                other.GetCollection("rows").Insert(Row(2));
                held.Set();
                release.Wait(TimeSpan.FromSeconds(20));
                other.Commit();
            }));
            owner.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            var before = db.GetSharedWaitDiagnostics(TimeSpan.FromHours(1)).Total;
            // The second handle reuses the wrapper and waits natively behind the other connection.
            var error = Record.Exception(() => db.BeginTransaction());
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(ownerError);
            Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(error).ErrorCode);
            var after = db.GetSharedWaitDiagnostics(TimeSpan.FromHours(1)).Total;
            Assert.Equal(before.TimedOut + 1, after.TimedOut);
            Assert.True(after.MaxWait >= TimeSpan.FromMilliseconds(300), $"Recorded wait {after.MaxWait} is shorter than the timed-out wait.");
        }
#pragma warning restore CS0618
    }
}
