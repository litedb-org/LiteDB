#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class DirectHostGroupCommit_Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Facades_share_covered_acknowledgements_and_terminal_barrier_failures(int failingSync)
        {
            using var file = new TempFile();
            var logPath = NativeFileSyncCollection.LogPath(file.Filename);
            Seed(file.Filename);
            var facades = Enumerable.Range(0, 3).Select(_ => new LiteDatabase(file.Filename)).ToArray();
            using var firstSync = new ManualResetEventSlim();
            using var secondSync = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            using var appended = new CountdownEvent(3);
            var tasks = new Task<Exception>[3];
            var syncs = 0;
            var acknowledgements = 0;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Signal();
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != logPath) return 0;
                var number = Interlocked.Increment(ref syncs);
                if (number == 1) { firstSync.Set(); Wait(releaseFirst); }
                if (number == 2) { secondSync.Set(); Wait(releaseSecond); }
                return number == failingSync ? 5 : 0;
            };
            try
            {
                tasks[0] = Start(() => Write(facades[0], 1));
                Wait(firstSync);
                tasks[1] = Start(() => Write(facades[1], 2));
                tasks[2] = Start(() => Write(facades[2], 3));
                appended.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                acknowledgements.Should().Be(0);
                foreach (var facade in facades) AssertVisible(facade, 0);
                releaseFirst.Set();
                if (failingSync != 1)
                {
                    Wait(secondSync);
                    await Completes(tasks[0]);
                    tasks[0].Result.Should().BeNull();
                    acknowledgements.Should().Be(1, "the first barrier covers only its captured prefix");
                    foreach (var facade in facades) AssertVisible(facade, 0, 1);
                    releaseSecond.Set();
                }
                await Completes(Task.WhenAll(tasks));
                syncs.Should().Be(failingSync == 1 ? 1 : 2);
                acknowledgements.Should().Be(failingSync == 0 ? 3 : failingSync - 1);
                if (failingSync == 0)
                {
                    tasks.Should().OnlyContain(task => task.Result == null);
                    foreach (var facade in facades) AssertVisible(facade, 0, 1, 2, 3);
                }
                else
                {
                    tasks.Skip(failingSync - 1).Should().OnlyContain(task => task.Result is IOException);
                    foreach (var facade in facades)
                    {
                        Action retry = () => facade.GetCollection("rows").Insert(Row(99));
                        retry.Should().Throw<IOException>();
                    }
                    Action attach = () => { using var peer = new LiteDatabase(file.Filename); };
                    attach.Should().Throw<IOException>();
                }
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                try { await Completes(Task.WhenAll(tasks.Where(task => task != null))); }
                finally
                {
                    EngineState.SimulateProcessCrash = null;
                    NativeFileSync.SimulateErrno = null;
                    foreach (var facade in facades) facade.Dispose();
                }
            }
            VerifyRecovery(file.Filename, failingSync == 0 ? new[] { 0, 1, 2, 3 } :
                failingSync == 2 ? new[] { 0, 1 } : new[] { 0 });

            void Write(LiteDatabase facade, int id)
            {
                facade.BeginTrans().Should().BeTrue();
                facade.GetCollection("rows").Insert(Row(id));
                facade.Commit().Should().BeTrue();
                Interlocked.Increment(ref acknowledgements);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1000)]
        public async Task Final_facade_close_fails_pending_writer_and_zero_frame_follower(int checkpoint)
        {
            using var file = new TempFile();
            var logPath = NativeFileSyncCollection.LogPath(file.Filename);
            Seed(file.Filename);
            using var first = new LiteDatabase(file.Filename);
            using var last = new LiteDatabase(file.Filename);
            first.CheckpointSize = checkpoint;
            using var firstSync = new ManualResetEventSlim();
            using var secondSync = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            using var appended = new CountdownEvent(2);
            using var noopReady = new ManualResetEventSlim();
            var tasks = new Task<Exception>[3];
            var syncs = 0;
            var groupBarriers = 0;
            var acknowledgements = 0;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Signal();
                if (phase == "wal-before-durable-flush") Interlocked.Increment(ref groupBarriers);
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != logPath) return 0;
                var number = Interlocked.Increment(ref syncs);
                if (number == 1) { firstSync.Set(); Wait(releaseFirst); }
                if (number == 2) { secondSync.Set(); Wait(releaseSecond); }
                return 0;
            };
            try
            {
                tasks[0] = Start(() =>
                {
                    first.GetCollection("rows").Insert(Row(1));
                    Interlocked.Increment(ref acknowledgements);
                });
                Wait(firstSync);
                tasks[1] = Start(() =>
                {
                    last.GetCollection("rows").Insert(Row(2));
                    Interlocked.Increment(ref acknowledgements);
                });
                appended.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                tasks[2] = Start(() =>
                {
                    last.BeginTrans().Should().BeTrue();
                    last.GetCollection("rows").Delete(99).Should().BeFalse();
                    noopReady.Set();
                    last.Commit().Should().BeTrue();
                    Interlocked.Increment(ref acknowledgements);
                });
                Wait(noopReady);
                acknowledgements.Should().Be(0);
                releaseFirst.Set();
                Wait(secondSync);
                await Completes(tasks[0]);
                tasks[0].Result.Should().BeNull();
                first.Dispose();
                acknowledgements.Should().Be(1);
                // Only the final facade still owns work. Closing it must not wait
                // on the held device barrier or acknowledge its pending tickets.
                await Completes(Task.Run(() => last.Dispose()));
                releaseSecond.Set();
                await Completes(Task.WhenAll(tasks));
                tasks.Skip(1).Should().OnlyContain(task => task.Result is IOException);
                acknowledgements.Should().Be(1);
                groupBarriers.Should().Be(2, "close must not retry the interrupted group");
                if (checkpoint == 0) syncs.Should().Be(2);
                else syncs.Should().BeGreaterThan(2, "the final close retains its normal checkpoint barriers");
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                try { await Completes(Task.WhenAll(tasks.Where(task => task != null))); }
                finally
                {
                    EngineState.SimulateProcessCrash = null;
                    NativeFileSync.SimulateErrno = null;
                }
            }
            VerifyRecovery(file.Filename, new[] { 0, 1 });
        }

        internal static void Seed(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.CheckpointSize = 0;
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(Row(0));
            db.Checkpoint();
        }

        internal static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id, ["payload"] = new string((char)('a' + id), 200)
        };

        private static void AssertVisible(LiteDatabase db, params int[] expected)
        {
            var rows = db.GetCollection("rows");
            rows.FindAll().Select(row => row["_id"].AsInt32).Should().BeEquivalentTo(expected);
            foreach (var id in Enumerable.Range(0, 4))
                rows.Find(Query.EQ("value", id)).Select(row => row["_id"].AsInt32).Should()
                    .BeEquivalentTo(expected.Where(value => value == id));
        }

        internal static void VerifyRecovery(string filename, int[] required)
        {
            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase(filename);
                var rows = db.GetCollection("rows");
                var documents = rows.FindAll().ToArray();
                documents.Select(row => row["_id"].AsInt32).Should().Contain(required).And.OnlyContain(id => id >= 0 && id <= 3);
                foreach (var row in documents)
                {
                    var id = row["_id"].AsInt32;
                    row["value"].AsInt32.Should().Be(id);
                    row["payload"].AsString.Should().Be(Row(id)["payload"].AsString);
                }
                foreach (var id in Enumerable.Range(0, 4))
                    rows.Find(Query.EQ("value", id)).Select(row => row["_id"].AsInt32).Should()
                        .BeEquivalentTo(documents.Where(row => row["_id"].AsInt32 == id).Select(row => row["_id"].AsInt32));
                db.Checkpoint();
            }
        }

        internal static void Wait(ManualResetEventSlim signal) => signal.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
        internal static Task<Exception> Start(Action action) => Task.Factory.StartNew(() =>
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        internal static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
