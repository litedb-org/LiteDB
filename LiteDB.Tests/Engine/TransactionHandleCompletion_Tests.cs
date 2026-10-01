using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Completion releases a handle's locks, leases, Shared writer ownership and registration
    /// whether or not the handle is disposed; maintenance waits for a lease after a thread hop.
    /// </summary>
    public class TransactionHandleCompletion_Tests
    {
        public enum Ending { Commit, Rollback, StatementFailure }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static ConnectionString Settings(TempFile file, bool shared) => new ConnectionString
        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };

        private static void End(ILiteTransaction tx, Ending ending)
        {
            var rows = tx.GetCollection("rows");
            switch (ending)
            {
                case Ending.Commit: tx.Commit(); break;
                case Ending.Rollback: tx.Rollback(); break;
                case Ending.StatementFailure:
                    Assert.Throws<LiteException>(() => rows.Insert(Row(1)));
                    Assert.Equal(LiteTransactionState.Failed, tx.State);
                    break;
            }
        }

        [Theory]
        [InlineData(false, Ending.Commit)] [InlineData(false, Ending.Rollback)] [InlineData(false, Ending.StatementFailure)]
        [InlineData(true, Ending.Commit)] [InlineData(true, Ending.Rollback)] [InlineData(true, Ending.StatementFailure)]
        public void Completed_undisposed_handle_releases_everything_it_owned(bool shared, Ending ending)
        {
            using var file = new TempFile();
            var settings = Settings(file, shared);
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").Insert(Row(1));
                db.Timeout = TimeSpan.FromSeconds(1);
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                End(tx, ending);
                // Kept alive, never disposed.
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                // Its collection lock and admission lease are gone: another thread writes the same
                // collection within the (short) lock timeout. In Shared mode this also needs the
                // native writer ownership the handle owned.
                var write = Task.Run(() => db.GetCollection("rows").Insert(Row(3)));
                Assert.True(write.Wait(TimeSpan.FromSeconds(20)), "The completed handle kept its ownership.");
                write.GetAwaiter().GetResult();
                if (!shared) db.Rebuild();
                GC.KeepAlive(tx);
                Assert.NotEqual(LiteTransactionState.Active, tx.State);
            }
            var expected = ending == Ending.Commit ? new[] { 1, 2, 3 } : new[] { 1, 3 };
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file });
                Assert.Equal(expected, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
        }

        [Fact]
        public void Rebuild_waits_for_a_lease_taken_on_another_thread_and_runs_after_completion()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file))
            {
                db.GetCollection("rows").Insert(Row(1));
                db.Timeout = TimeSpan.FromSeconds(30);
                // Begin on one thread, write on another, so the lease is not the rebuild thread's.
                var tx = Task.Run(() => db.BeginTransaction()).Result;
                Task.Run(() => tx.GetCollection("rows").Insert(Row(2))).Wait();
                var rebuild = Task.Run(() => db.Rebuild());
                Assert.False(rebuild.Wait(500), "Rebuild bypassed the handle's admission lease.");
                Task.Run(() => tx.Commit()).Wait();
                Assert.True(rebuild.Wait(TimeSpan.FromSeconds(30)));
                Assert.Equal(new[] { 1, 2 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 2 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Theory]
        [InlineData(":memory:")]
        [InlineData(":temp:")]
        public void Handles_work_on_memory_and_temporary_storage(string filename)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename });
            db.GetCollection("rows").Insert(Row(1));
            var committed = Task.Run(() => db.BeginTransaction()).Result;
            Task.Run(() => committed.GetCollection("rows").Insert(Row(2))).Wait();
            Task.Run(() => committed.Commit()).Wait();
            using (var rolledBack = db.BeginTransaction())
            {
                rolledBack.GetCollection("rows").Insert(Row(3));
                Task.Run(() => Assert.Equal(3, rolledBack.GetCollection("rows").Count())).Wait();
                rolledBack.Rollback();
            }
            using (var disposed = db.BeginTransaction()) disposed.GetCollection("rows").Insert(Row(4));
            Assert.Equal(new[] { 1, 2 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            Assert.Equal(LiteTransactionState.Committed, committed.State);
        }
    }
}
