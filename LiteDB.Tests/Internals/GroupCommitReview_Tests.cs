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
    public class GroupCommitReview_Tests
    {
        [Fact]
        public async Task Ensuring_a_pending_index_must_not_acknowledge_without_syncing_its_dependency()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.Checkpoint();
            var data = TempFile.ReadAllBytesShared(file.Filename);
            var log = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
            var group = Disk(engine).CommitGroup;
            var sync = typeof(WalCommitGroup).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            using var appended = new ManualResetEventSlim();
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Set();
            };
            var syncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != FileHelper.GetLogFile(file.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                return 5;
            };
            Task<Exception> first = null;
            var secondAcknowledged = false;
            var barriersAtSecondAcknowledgement = -1;
            Exception secondError = null;
            try
            {
                // Model A being descheduled after releasing its snapshots and
                // before acquiring the sync monitor. B runs first on that monitor.
                lock (sync)
                {
                    first = Task.Factory.StartNew(() =>
                    {
                        try { rows.EnsureIndex("value"); return null; }
                        catch (Exception error) { return error; }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    appended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    try
                    {
                        rows.EnsureIndex("value").Should().BeFalse();
                        secondAcknowledged = true;
                        barriersAtSecondAcknowledgement = Volatile.Read(ref syncs);
                    }
                    catch (Exception error) { secondError = error; }
                }
                await Completes(first);
                (await first).Should().BeAssignableTo<IOException>();
            }
            finally
            {
                if (first != null) await Completes(first);
                NativeFileSync.SimulateErrno = null;
                EngineState.SimulateProcessCrash = null;
            }

            // Power-loss model: retain the checkpointed image and lose every
            // write since it. An EnsureIndex acknowledged by B must survive.
            using var recoveredFile = new TempFile();
            File.WriteAllBytes(recoveredFile.Filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), log);
            using var recovered = new LiteDatabase(recoveredFile.Filename);
            recovered.GetCollection("rows").Count().Should().Be(1);
            if (secondAcknowledged)
            {
                recovered.GetCollection("$indexes").FindAll().Where(index => index["collection"] == "rows")
                    .Select(index => index["name"].AsString).Should().Contain("value",
                        "B successfully ensured this index before A's failed barrier; the model loses only unsynced bytes");
                barriersAtSecondAcknowledgement.Should().BeGreaterThan(0,
                    "EnsureIndex observed A's pending index and returned success, but no sync established that index");
            }
            else secondError.Should().BeAssignableTo<IOException>();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Noop_delete_and_explicit_commit_must_cover_the_pending_version_they_observed(bool explicitTransaction)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.Checkpoint();
            var data = TempFile.ReadAllBytesShared(file.Filename);
            var log = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
            var group = Disk(engine).CommitGroup;
            var sync = typeof(WalCommitGroup).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            using var appended = new ManualResetEventSlim();
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Set();
            };
            var syncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != FileHelper.GetLogFile(file.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                return 5;
            };
            Task<Exception> first = null;
            var acknowledged = false;
            Exception secondError = null;
            try
            {
                lock (sync)
                {
                    first = Task.Factory.StartNew(() =>
                    {
                        try { rows.Delete(1).Should().BeTrue(); return null; }
                        catch (Exception error) { return error; }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    appended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    try
                    {
                        if (explicitTransaction) db.BeginTrans().Should().BeTrue();
                        rows.Delete(1).Should().BeFalse();
                        if (explicitTransaction)
                        {
                            Assert.Null(rows.FindById(1));
                            db.Commit().Should().BeTrue();
                        }
                        acknowledged = true;
                        Volatile.Read(ref syncs).Should().Be(0);
                    }
                    catch (Exception error) { secondError = error; }
                }
                await Completes(first);
                (await first).Should().BeAssignableTo<IOException>();
            }
            finally
            {
                if (first != null) await Completes(first);
                NativeFileSync.SimulateErrno = null;
                EngineState.SimulateProcessCrash = null;
            }
            using var recoveredFile = new TempFile();
            File.WriteAllBytes(recoveredFile.Filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), log);
            using var recovered = new LiteDatabase(recoveredFile.Filename);
            recovered.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
            if (acknowledged)
                Assert.Null(recovered.GetCollection("rows").FindById(1));
            else secondError.Should().BeAssignableTo<IOException>();
        }

        private static DiskService Disk(LiteEngine engine) => (DiskService)typeof(LiteEngine)
            .GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);

        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
