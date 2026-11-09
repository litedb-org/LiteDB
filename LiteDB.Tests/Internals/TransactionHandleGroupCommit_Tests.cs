#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleGroupCommit_Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Handle_tickets_cover_captured_prefix_and_zero_frame_dependencies(int failingSync)
        {
            using var file = new TempFile();
            var path = NativeFileSyncCollection.LogPath(file.Filename);
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(TransactionHandleProcessHost_Tests.Row(0));
                seed.GetCollection("rows").EnsureIndex("value");
                seed.Checkpoint();
            }
            var engines = Enumerable.Range(0, 3).Select(_ => new LiteEngine(file.Filename)).ToArray();
            var databases = engines.Select(e => new LiteDatabase(e, disposeOnClose: false)).ToArray();
            var handles = databases.Select(db => db.BeginTransaction()).ToArray();
            using var firstSync = new ManualResetEventSlim();
            using var secondSync = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            using var appended = new CountdownEvent(2);
            using var noopReady = new ManualResetEventSlim();
            var tasks = new Task<Exception>[3];
            var syncs = 0;
            var acknowledged = 0;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Signal();
            };
            NativeFileSync.SimulateErrno = name =>
            {
                if (name != path) return 0;
                var number = Interlocked.Increment(ref syncs);
                if (number == 1) { firstSync.Set(); Wait(releaseFirst); }
                if (number == 2) { secondSync.Set(); Wait(releaseSecond); }
                return number == failingSync ? 5 : 0;
            };
            Task<Exception> Start(int index, bool noop = false) => Task.Factory.StartNew(() =>
            {
                try
                {
                    if (noop)
                    {
                        Assert.False(handles[index].GetCollection("rows").Delete(99));
                        noopReady.Set();
                    }
                    else handles[index].GetCollection("rows").Insert(TransactionHandleProcessHost_Tests.Row(index + 1));
                    handles[index].Commit();
                    Assert.Equal(LiteTransactionState.Committed, handles[index].State);
                    Interlocked.Increment(ref acknowledged);
                    return null;
                }
                catch (Exception error) { return error; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                tasks[0] = Start(0);
                Wait(firstSync);
                tasks[1] = Start(1);
                Assert.True(appended.Wait(TimeSpan.FromSeconds(15)));
                tasks[2] = Start(2, true);
                Wait(noopReady);
                Assert.Equal(0, acknowledged);
                Assert.All(handles, handle => Assert.Contains(handle.State, new[] { LiteTransactionState.Active, LiteTransactionState.Indeterminate }));
                Assert.All(databases, db => Assert.Equal(1, db.TransactionHandles.ActiveCount));
                Assert.Equal(1, TransactionHandleProcessHost_Tests.Count(engines[0], "rows"));
                releaseFirst.Set();
                if (failingSync != 1)
                {
                    Wait(secondSync);
                    await Complete(tasks[0]);
                    Assert.Null(tasks[0].Result);
                    Assert.Equal(1, acknowledged);
                    Assert.False(tasks[2].IsCompleted);
                    Assert.Equal(LiteTransactionState.Committed, handles[0].State);
                    Assert.Equal(0, databases[0].TransactionHandles.ActiveCount);
                    for (var follower = 1; follower < 3; follower++)
                    {
                        Assert.Contains(handles[follower].State, new[] { LiteTransactionState.Active, LiteTransactionState.Indeterminate });
                        Assert.Equal(1, databases[follower].TransactionHandles.ActiveCount);
                    }
                    Assert.Equal(2, TransactionHandleProcessHost_Tests.Count(engines[0], "rows"));
                    releaseSecond.Set();
                }
                await Complete(Task.WhenAll(tasks));
                Assert.Equal(failingSync == 0 ? 3 : failingSync - 1, acknowledged);
                Assert.Equal(failingSync == 1 ? 1 : 2, syncs);
                Assert.All(databases, db => Assert.Equal(0, db.TransactionHandles.ActiveCount));
                Assert.Empty(engines[0].HandleMonitor.Transactions);
                if (failingSync == 0) Assert.All(tasks, task => Assert.Null(task.Result));
                else
                {
                    Assert.All(tasks.Skip(failingSync - 1), task => Assert.IsAssignableFrom<IOException>(task.Result));
                    Assert.All(handles.Skip(failingSync - 1), handle => Assert.Equal(LiteTransactionState.Indeterminate, handle.State));
                    foreach (var engine in engines)
                        Assert.ThrowsAny<IOException>(() => engine.Insert("escape", new[] { TransactionHandleProcessHost_Tests.Row(99) }, BsonAutoId.Int32));
                }
            }
            finally
            {
                releaseFirst.Set(); releaseSecond.Set();
                try { await Complete(Task.WhenAll(tasks.Where(t => t != null))); }
                finally
                {
                    EngineState.SimulateProcessCrash = null;
                    NativeFileSync.SimulateErrno = null;
                    foreach (var handle in handles) handle.Dispose();
                    foreach (var db in databases) db.Dispose();
                    foreach (var engine in engines) engine.Dispose();
                }
            }
            for (var open = 0; open < 2; open++)
            {
                using var recovered = new LiteDatabase(file.Filename);
                var rows = recovered.GetCollection("rows");
                Assert.NotNull(rows.FindById(0));
                if (failingSync != 1) Assert.NotNull(rows.FindById(1));
                if (failingSync == 0) Assert.NotNull(rows.FindById(2));
                var ids = rows.FindAll().Select(d => d["_id"].AsInt32).OrderBy(id => id).ToArray();
                Assert.All(ids, id => Assert.InRange(id, 0, 2));
                Assert.Equal(ids, rows.Find("value >= 0").Select(d => d["_id"].AsInt32).OrderBy(id => id).ToArray());
                Assert.Equal(0, recovered.GetCollection("escape").Count());
                recovered.Checkpoint();
            }
        }

        private static void Wait(ManualResetEventSlim signal) => Assert.True(signal.Wait(TimeSpan.FromSeconds(15)));
        private static async Task Complete(Task task)
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))));
            await task;
        }
    }
}
#endif
