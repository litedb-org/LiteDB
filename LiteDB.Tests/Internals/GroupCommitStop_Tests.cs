#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitStop_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Uncertain_follower_stops_the_captured_leader_and_zero_frame_waiter(
            bool syncStarted, bool ioFailure)
        {
            using var file = new TempFile();
            var logPath = NativeFileSyncCollection.LogPath(file.Filename);
            Seed(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            var state = State(engine);
            using var captured = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var noop = new ManualResetEventSlim();
            var syncs = 0;
            var directorySyncs = 0;
            NativeFileSync.SimulateDirectoryErrno = path => { Interlocked.Increment(ref directorySyncs); return 0; };
            var writes = 0;
            var acknowledgements = 0;
            Task<Exception> leader = null, waiter = null;
            state.AtCrashPoint = phase =>
            {
                if (!syncStarted && phase == "wal-before-durable-flush")
                {
                    captured.Set();
                    release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                }
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != logPath) return 0;
                Interlocked.Increment(ref syncs);
                if (syncStarted)
                {
                    captured.Set();
                    release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                }
                return 0;
            };
            try
            {
                leader = Start(() => { rows.Update(Row(1, 1)).Should().BeTrue(); Interlocked.Increment(ref acknowledgements); });
                captured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                waiter = Start(() =>
                {
                    db.BeginTrans().Should().BeTrue();
                    rows.Delete(99).Should().BeFalse();
                    // A zero-frame transaction still depends on the pending version.
                    noop.Set();
                    db.Commit().Should().BeTrue();
                    Interlocked.Increment(ref acknowledgements);
                });
                noop.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                state.AtCrashPoint = phase =>
                {
                    if (phase == "wal-confirmation-after-write")
                        throw ioFailure ? new IOException("uncertain confirmation") : new InvalidOperationException("uncertain confirmation");
                };
                engine.SimulateDiskWriteFail = page => Interlocked.Increment(ref writes);
                var beforeStop = 0;
                Exception hookFailure = null;
                state.AfterFailedWalWrite = () =>
                {
                    try
                    {
                        state.Stopped.Should().BeTrue();
                        beforeStop = Volatile.Read(ref writes);
                        release.Set();
                        Task.WaitAll(new Task[] { leader, waiter }, TimeSpan.FromSeconds(10)).Should().BeTrue(
                            "pending members fail before the failing writer starts teardown");
                        leader.Result.Should().BeAssignableTo<IOException>();
                        waiter.Result.Should().BeAssignableTo<IOException>();
                        Action later = () => rows.Update(Row(4, 4));
                        later.Should().Throw<IOException>();
                        Volatile.Read(ref writes).Should().Be(beforeStop);
                    }
                    catch (Exception error) { hookFailure = error; }
                };
                Action uncertain = () => rows.Update(Row(2, 2));
                uncertain.Should().Throw<IOException>();
                await Completes(Task.WhenAll(leader, waiter));
                hookFailure.Should().BeNull();
                beforeStop.Should().BeGreaterThan(0, "the uncertain batch and teardown-window hook must execute");
                acknowledgements.Should().Be(0);
                syncs.Should().Be(syncStarted ? 1 : 0, "a captured leader cannot start a sync after another writer stopped the engine");
                directorySyncs.Should().Be(0, "an in-flight device sync cannot start a directory barrier after terminal stop");
            }
            finally
            {
                release.Set();
                if (leader != null) await Completes(leader);
                if (waiter != null) await Completes(waiter);
                NativeFileSync.SimulateErrno = null;
                NativeFileSync.SimulateDirectoryErrno = null;
                state.AtCrashPoint = null;
                state.AfterFailedWalWrite = null;
                engine.SimulateDiskWriteFail = null;
            }
            // A stopped process host refuses reattachment until its last facade closes.
            engine.Dispose();
            VerifyRecovery(file.Filename);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Failed_leader_buffer_flush_publishes_stop_under_the_writer_monitor(bool ioFailure)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            var disk = Disk(engine);
            var state = State(engine);
            using var attempted = new ManualResetEventSlim();
            Task<Exception> queued = null;
            var stoppedAtWriterAdmission = false;
            var hits = 0;
            var syncs = 0;
            NativeFileSync.SimulateErrno = path => { Interlocked.Increment(ref syncs); return 0; };
            state.AtCrashPoint = phase =>
            {
                if (phase != "wal-before-batch-flush") return;
                Interlocked.Increment(ref hits);
                queued = Start(() =>
                {
                    attempted.Set();
                    lock (disk.WalWriterLock)
                    {
                        stoppedAtWriterAdmission = state.Stopped;
                        state.Validate();
                    }
                });
                attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                throw ioFailure ? new IOException("failed leader padding/flush") : new InvalidOperationException("failed leader padding/flush");
            };
            try
            {
                Action commit = () => rows.Update(Row(1, 1));
                commit.Should().Throw<IOException>();
                queued.Should().NotBeNull();
                await Completes(queued);
                queued.Result.Should().BeAssignableTo<IOException>();
                stoppedAtWriterAdmission.Should().BeTrue();
                hits.Should().Be(1);
                syncs.Should().Be(0);
                state.WriteFailure.Should().NotBeNull();
                state.WriteFailure.Cause.Message.Should().Contain(ioFailure ? "failed leader" : "WAL durable flush failed");
                Action later = () => rows.Update(Row(4, 4));
                later.Should().Throw<IOException>();
            }
            finally
            {
                if (queued != null) await Completes(queued);
                state.AtCrashPoint = null;
                NativeFileSync.SimulateErrno = null;
            }
            // A stopped process host refuses reattachment until its last facade closes.
            engine.Dispose();
            VerifyRecovery(file.Filename);
        }

        private static void Seed(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value");
            rows.Insert(Enumerable.Range(1, 4).Select(id => Row(id, 0)));
            db.Checkpoint();
        }

        private static void VerifyRecovery(string filename)
        {
            // These commits were outcome unknown. Each row must be a complete old/new
            // value, indexed identically; the acknowledged seed and unrelated row survive.
            for (var open = 0; open < 2; open++)
            {
                using var copy = new LiteDatabase(filename);
                var rows = copy.GetCollection("rows");
                var documents = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                documents.Select(row => row["_id"].AsInt32).Should().Equal(1, 2, 3, 4);
                foreach (var row in documents)
                {
                    var id = row["_id"].AsInt32;
                    row["payload"].AsString.Should().Be(new string((char)('a' + id), 200));
                    row["value"].AsInt32.Should().BeOneOf(0, id <= 2 ? id : 0);
                }
                foreach (var value in new[] { 0, 1, 2 })
                    rows.Find(Query.EQ("value", value)).Select(row => row["_id"].AsInt32).Should()
                        .BeEquivalentTo(documents.Where(row => row["value"].AsInt32 == value).Select(row => row["_id"].AsInt32));
                copy.Checkpoint();
            }
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + id), 200)
        };
        private static DiskService Disk(LiteEngine engine) => (DiskService)typeof(LiteEngine)
            .GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
        private static EngineState State(LiteEngine engine) => (EngineState)typeof(LiteEngine)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
        private static Task<Exception> Start(Action action) => Task.Factory.StartNew(() =>
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
