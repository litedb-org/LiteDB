using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleMaintenanceProgress_Tests
    {
        [Fact]
        public void Queued_maintenance_precedes_later_handle_admission_after_foreign_completion()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            engine.Pragma(Pragmas.TIMEOUT, 10);
            var first = engine.BeginHandleTransaction();
            first.Invoke(e => e.Insert("rows", new[] { TransactionHandleProcessHost_Tests.Row(1) }, BsonAutoId.Int32));
            var locker = typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            var gate = locker.GetType().GetField("_transaction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(locker);
            var waiting = gate.GetType().GetField("_waitingWriters", BindingFlags.Instance | BindingFlags.NonPublic);
            var maintenance = Task.Run(() => engine.Checkpoint());
            Assert.True(SpinWait.SpinUntil(() => (int)waiting.GetValue(gate) > 0, TimeSpan.FromSeconds(5)));
            var next = Task.Run(() => engine.BeginHandleTransaction());
            Assert.False(next.IsCompleted);
            Assert.True(Task.Run(() => first.Invoke(e => e.Commit())).GetAwaiter().GetResult());
            Assert.True(maintenance.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(next.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(next.Result.Invoke(e => e.Rollback()));
            Assert.Equal(1, TransactionHandleProcessHost_Tests.Count(engine, "rows"));
        }
    }
}
