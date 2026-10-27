#if NET8_0_OR_GREATER
using System;
using System.Collections.Concurrent;
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
    public class GroupCommit_Tests
    {
        [Fact]
        public void Descriptor_sync_does_not_flush_a_later_writers_managed_buffer()
        {
            using var file = new TempFile();
            using (var stream = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 8192))
            {
                stream.WriteByte(42);
                stream.Flush();
                var handle = stream.SafeFileHandle;
                // A subsequent appender owns this buffer while the leader syncs
                // its captured prefix. Touching SafeFileHandle again flushes it.
                stream.WriteByte(99);
                new FileInfo(file.Filename).Length.Should().Be(1);
                NativeFileSync.FlushDevice(handle, stream.Name);
                TempFile.ReadAllBytesShared(file.Filename).Should().Equal(42);
                stream.Flush();
            }
            File.ReadAllBytes(file.Filename).Should().Equal(42, 99);
        }

        [Theory]
        [InlineData(4, false, false, 0)]
        [InlineData(8, false, true, 0)]
        [InlineData(4, true, true, 0)]
        [InlineData(4, false, false, 1)]
        [InlineData(4, false, true, 2)]
        public async Task Concurrent_commits_share_barriers_and_preserve_durable_visibility(
            int writers, bool collections, bool explicitTransaction, int failingSync)
        {
            using var file = new TempFile();
            var logPath = FileHelper.GetLogFile(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            string Name(int id) => collections ? "rows" + id : "rows";
            for (var id = 1; id <= writers; id++)
            {
                var rows = db.GetCollection(Name(id));
                rows.EnsureIndex("value");
                rows.Upsert(new BsonDocument { ["_id"] = 0, ["value"] = 0 });
            }
            db.Checkpoint();
            using var firstSync = new ManualResetEventSlim();
            using var secondSync = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            using var appended = new CountdownEvent(writers);
            var syncs = 0;
            var acknowledgements = 0;
            byte[] durableData = null, secondData = null, firstWal = null, secondWal = null;
            var commitThreads = new ConcurrentDictionary<int, byte>();
            var tasks = new Task<Exception>[writers];
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Signal();
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != logPath || !commitThreads.ContainsKey(Thread.CurrentThread.ManagedThreadId)) return 0;
                var number = Interlocked.Increment(ref syncs);
                if (number == 1)
                {
                    durableData = Read(file.Filename);
                    firstWal = Read(logPath);
                    firstSync.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                }
                else if (number == 2)
                {
                    secondData = Read(file.Filename);
                    secondWal = Read(logPath);
                    secondSync.Set();
                    releaseSecond.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                }
                return number == failingSync ? 5 : 0;
            };
            Task<Exception> Start(int id) => Task.Factory.StartNew(() =>
            {
                try
                {
                    commitThreads.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
                    if (explicitTransaction) db.BeginTrans();
                    db.GetCollection(Name(id)).Insert(new BsonDocument { ["_id"] = id, ["value"] = id });
                    if (explicitTransaction) db.Commit();
                    Interlocked.Increment(ref acknowledgements);
                    return null;
                }
                catch (Exception error) { return error; }
                finally { commitThreads.TryRemove(Thread.CurrentThread.ManagedThreadId, out _); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                tasks[0] = Start(1);
                firstSync.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                for (var id = 2; id <= writers; id++) tasks[id - 1] = Start(id);
                appended.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("followers append while the leader syncs");
                acknowledgements.Should().Be(0);
                // No user snapshot pins the pre-sync version here. Checkpoint must
                // retain it for future readers while the pending writers wait.
                var checkpoint = Task.Run(() => engine.Checkpoint());
                await Completes(checkpoint);
                for (var id = 1; id <= writers; id++) db.GetCollection(Name(id)).Count().Should().Be(1);
                VerifyImage(durableData, firstWal, writers, collections, 1);
                releaseFirst.Set();
                if (failingSync != 1)
                {
                    secondSync.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                    await Completes(tasks[0]);
                    acknowledgements.Should().Be(1, "the first barrier cannot acknowledge later appends");
                    for (var id = 1; id <= writers; id++)
                        db.GetCollection(Name(id)).Count().Should().Be(collections && id != 1 ? 1 : 2);
                    VerifyImage(secondData, secondWal, writers, collections, writers);
                    if (failingSync == 0 && !collections && writers == 4)
                        GroupCommitRecovery.CheckUnsyncedTails(durableData, firstWal, secondWal, writers);
                    releaseSecond.Set();
                }
                var all = Task.WhenAll(tasks);
                await Completes(all);
                var failures = await all;
                if (failingSync == 0)
                {
                    failures.Should().OnlyContain(error => error == null);
                    syncs.Should().Be(2, "the followers share the next sync without a timed batching delay");
                    acknowledgements.Should().Be(writers);
                    VerifyImage(Read(file.Filename), Read(logPath), writers, collections, writers);
                }
                else
                {
                    syncs.Should().Be(failingSync, "a failed barrier stops the next leader before it syncs");
                    failures.Skip(failingSync == 2 ? 1 : 0).Should().OnlyContain(error => error is IOException);
                    acknowledgements.Should().Be(failingSync == 1 ? 0 : 1);
                    var state = (EngineState)typeof(LiteEngine).GetField("_state",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine);
                    state.Stopped.Should().BeTrue();
                    state.WriteFailure.Should().NotBeNull();
                    state.WriteFailure.File.Should().Be("log");
                    state.WriteFailure.Cause.Should().BeOfType<FileSyncException>();
                    Action retry = () => db.GetCollection(Name(1)).Insert(new BsonDocument { ["_id"] = 99 });
                    retry.Should().Throw<IOException>();
                    // Modeled loss of every byte beyond the last successful barrier.
                    if (failingSync == 2) VerifyImage(durableData, firstWal, writers, collections, 1);
                }
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                var active = tasks.Where(task => task != null).ToArray();
                try { await Completes(Task.WhenAll(active)); }
                finally
                {
                    NativeFileSync.SimulateErrno = null;
                    EngineState.SimulateProcessCrash = null;
                }
            }
        }

        [Theory]
        [InlineData(CompactStorageMode.Auto)]
        [InlineData(CompactStorageMode.Legacy)]
        public async Task Grouped_multi_collection_transactions_preserve_rollback_indexes_and_pinned_readers(
            CompactStorageMode storage)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, CompactStorage = storage }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                var audit = db.GetCollection("audit");
                rows.EnsureIndex("value");
                audit.EnsureIndex("value");
                for (var worker = 1; worker <= 4; worker++)
                    rows.Insert(new BsonDocument { ["_id"] = worker, ["value"] = -1 });
                db.Checkpoint();
                db.CheckpointSize = 32;
                using (var pinned = rows.FindAll().GetEnumerator())
                {
                    pinned.MoveNext().Should().BeTrue();
                    var first = pinned.Current;
                    var tasks = Enumerable.Range(1, 4).Select(worker => Task.Factory.StartNew(() =>
                    {
                        for (var step = 0; step < 16; step++)
                        {
                            db.BeginTrans();
                            rows.Update(new BsonDocument { ["_id"] = worker, ["value"] = step });
                            audit.Insert(new BsonDocument { ["_id"] = worker * 100 + step, ["value"] = step });
                            if (step % 3 == 0) db.Rollback();
                            else db.Commit();
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                    await Completes(Task.WhenAll(tasks));
                    first["value"].AsInt32.Should().Be(-1);
                    while (pinned.MoveNext()) pinned.Current["value"].AsInt32.Should().Be(-1);
                    engine.Checkpoint();
                }
            }
            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").FindAll().Should().HaveCount(4)
                    .And.OnlyContain(row => row["value"].AsInt32 == 14);
                db.GetCollection("rows").Find(Query.EQ("value", 14)).Should().HaveCount(4);
                var expected = Enumerable.Range(1, 4).SelectMany(worker => Enumerable.Range(0, 16)
                    .Where(step => step % 3 != 0).Select(step => worker * 100 + step));
                db.GetCollection("audit").FindAll().Select(row => row["_id"].AsInt32)
                    .Should().BeEquivalentTo(expected);
                db.GetCollection("audit").Find(Query.EQ("value", 15)).Should().BeEmpty("that transaction rolled back");
                db.Checkpoint();
            }
        }

        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25)))).Should().BeSameAs(task);
            await task;
        }

        internal static byte[] Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static void VerifyImage(byte[] dataBytes, byte[] logBytes, int writers, bool collections, int committed)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, dataBytes);
            File.WriteAllBytes(FileHelper.GetLogFile(file.Filename), logBytes);
            // No graceful close of the source engine; reopen the captured files,
            // checkpoint, then reopen again to prove recovery is repeatable.
            for (var open = 0; open < 2; open++)
            {
                using var copy = new LiteDatabase(file.Filename);
                for (var id = 1; id <= (collections ? writers : 1); id++)
                {
                    var rows = copy.GetCollection(collections ? "rows" + id : "rows");
                    var expected = collections ? (id <= committed ? new[] { 0, id } : new[] { 0 }) :
                        Enumerable.Range(0, committed + 1).ToArray();
                    rows.FindAll().Select(row => row["_id"].AsInt32).Should().BeEquivalentTo(expected);
                    rows.Find(Query.GT("value", 0)).Select(row => row["value"].AsInt32)
                        .Should().BeEquivalentTo(expected.Where(value => value != 0));
                }
                copy.Checkpoint();
            }
        }
    }
}
#endif
