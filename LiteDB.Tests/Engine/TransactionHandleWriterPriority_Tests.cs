using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A thread holding a cursor lease that a queued exclusive writer waits for can begin a
    /// handle, as a legacy BeginTrans in the same reader loop can.
    /// </summary>
    public class TransactionHandleWriterPriority_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static int WaitingWriters(LiteDatabase db)
        {
            object engine = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(db);
            engine = engine as LiteEngine ?? engine.GetType().GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var locker = typeof(LiteEngine).GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var gate = locker.GetType().GetField("_transaction", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(locker);
            return (int)gate.GetType().GetField("_waitingWriters", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gate);
        }

        // Checkpoint waits for readers only briefly, then works around them; rebuild queues.
        [Fact]
        public void Begin_on_a_thread_holding_a_cursor_lease_is_not_queued_behind_a_waiting_rebuild()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file) { Timeout = TimeSpan.FromSeconds(5) })
            {
                db.GetCollection("rows").Insert(Row(1));
                db.GetCollection("rows").Insert(Row(2));
                using var reader = db.GetCollection("rows").FindAll().GetEnumerator();
                Assert.True(reader.MoveNext());
                Exception maintenanceError = null;
                var maintenance = new Thread(() =>
                {
                    try { db.Rebuild(); }
                    catch (Exception error) { maintenanceError = error; }
                });
                maintenance.Start();
                Assert.True(SpinWait.SpinUntil(() => WaitingWriters(db) == 1, TimeSpan.FromSeconds(10)));
                var started = DateTime.UtcNow;
                using (var tx = db.BeginTransaction())
                {
                    Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "The begin queued behind the writer.");
                    tx.GetCollection("rows").Insert(Row(3));
                    tx.Commit();
                }
                Assert.True(maintenance.IsAlive, "Rebuild did not wait for the cursor.");
                reader.Dispose();
                Assert.True(maintenance.Join(TimeSpan.FromSeconds(20)));
                Assert.Null(maintenanceError);
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 2, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }
    }
}
