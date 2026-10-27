#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileGroupDowngrade_Tests
    {
        [OneFileTheory]
        [InlineData(0, "follower", null)]
        [InlineData(22, "follower", null)]
        [InlineData(30, "follower", null)]
        [InlineData(22, "noop", null)]
        [InlineData(22, "error", null)]
        [InlineData(22, "query", null)]
        [InlineData(22, "noop", "batch")]
        [InlineData(22, "noop", "device")]
        public void Issued_mapped_tickets_complete_after_downgrade_without_publishing_uncovered_versions(
            int errno, string completion, string failure)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var shared = new LiteDatabase(engine, disposeOnClose: false))
            {
                shared.BeginTrans().Should().BeTrue(); // retain native main + WRITE for this forced schedule
                var core = Core(engine);
                core.Rollback().Should().BeTrue();
                using var db = new LiteDatabase(core, disposeOnClose: false);
                db.CheckpointSize = 0;
                var state = State(core);
                var disk = Field<DiskService>(core, "_disk");
                var group = disk.CommitGroup;
                group.Should().NotBeNull();
                var table = engine.MappedReaderTable;
                var initial = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
                using var leaderCaptured = new ManualResetEventSlim();
                using var resumeLeader = new ManualResetEventSlim();
                using var followerPaused = new ManualResetEventSlim();
                using var resumeFollower = new ManualResetEventSlim();
                var followerThread = 0;
                var syncs = 0;
                var publications = 0;
                state.AtCrashPoint = stage =>
                {
                    var pause = completion == "follower" ? "wal-after-index-confirmation" : "wal-before-ticket-completion";
                    if (stage == pause && Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref followerThread))
                    {
                        followerPaused.Set();
                        resumeFollower.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                    if (stage == "wal-before-batch-flush" && Volatile.Read(ref syncs) == 1 && failure == "batch")
                        throw new IOException("degraded mapped managed flush failure");
                };
                NativeFileSync.SimulateErrno = path =>
                {
                    if (path != files.Log) return 0;
                    var number = Interlocked.Increment(ref syncs);
                    if (number == 1)
                    {
                        leaderCaptured.Set();
                        resumeLeader.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                    return number > 1 && failure == "device" ? 5 : errno;
                };
                Action<string> observe = stage =>
                {
                    if (stage != "commit-version") return;
                    Volatile.Read(ref syncs).Should().BeGreaterThan(0);
                    errno.Should().Be(0, "unsupported device sync must retain the mapped recovery fence");
                    Interlocked.Increment(ref publications);
                };
                Task<Exception> leader = null, follower = null;
                try
                {
                    leader = Start(() => db.GetCollection("docs").Update(Row(1, 1)).Should().BeTrue(), observe);
                    leaderCaptured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    follower = Start(() =>
                    {
                        Volatile.Write(ref followerThread, Thread.CurrentThread.ManagedThreadId);
                        db.GetCollection("docs").Update(Row(2, 2)).Should().BeTrue();
                    }, observe);
                    followerPaused.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                    resumeLeader.Set();
                    Finish(leader).Should().BeNull();
                    group.PendingVersions.Should().NotBeEmpty();
                    if (errno == 0) publications.Should().BeGreaterThan(0);
                    else
                    {
                        disk.CommitGroup.Should().BeNull();
                        disk.IsLogFlushDurable.Should().BeFalse();
                        table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                        Fenced(table);
                    }
                    if (completion != "follower")
                    {
                        var length = disk.GetFileLength(FileOrigin.Log);
                        Action dependent = () => Complete(db, core, completion);
                        SharedMemoryReaderTable.PublicationStage = observe;
                        try
                        {
                            if (failure == null) dependent();
                            else dependent.Should().Throw<IOException>();
                        }
                        finally { SharedMemoryReaderTable.PublicationStage = null; }
                        disk.GetFileLength(FileOrigin.Log).Should().Be(length);
                    }
                    resumeFollower.Set();
                    var error = Finish(follower);
                    if (failure == null)
                    {
                        error.Should().BeNull();
                        group.PendingVersions.Should().BeEmpty();
                        disk.CommitReadVersionLimit.Should().Be(int.MaxValue);
                        Volatile.Read(ref syncs).Should().Be(2);
                        VerifyRows(db, true);
                    }
                    else
                    {
                        error.Should().BeAssignableTo<IOException>();
                        state.Stopped.Should().BeTrue();
                    }
                    if (errno != 0)
                    {
                        publications.Should().Be(0);
                        table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                        Fenced(table);
                    }
                    // Capture before rollback/disposal can add a barrier or
                    // checkpoint. This models restart from surviving OS cache.
                    using var saved = new TempFile();
                    File.WriteAllBytes(saved.Filename, TempFile.ReadAllBytesShared(files.Filename));
                    File.WriteAllBytes(FileHelper.GetLogFile(saved.Filename), TempFile.ReadAllBytesShared(files.Log));
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        using var recovered = new LiteDatabase(saved.Filename);
                        VerifyRows(recovered, failure == null);
                        recovered.Checkpoint();
                    }
                }
                finally
                {
                    resumeLeader.Set(); resumeFollower.Set();
                    if (leader != null) Finish(leader);
                    if (follower != null) Finish(follower);
                    state.AtCrashPoint = null;
                    NativeFileSync.SimulateErrno = null;
                    try { shared.Rollback(); } catch (IOException) when (failure != null) { }
                }
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(files.Filename);
                VerifyRows(cold, failure == null);
                cold.Checkpoint();
            }
        }

        private static void Complete(LiteDatabase db, LiteEngine core, string operation)
        {
            if (operation == "noop") db.GetCollection("docs").Delete(999).Should().BeFalse();
            else if (operation == "error")
            {
                Action insert = () => db.GetCollection("docs").Insert(Row(2, 2));
                insert.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            }
            else
            {
                using var reader = core.Query("docs", new Query { ForUpdate = true });
                var count = 0;
                while (reader.Read()) count++;
                count.Should().Be(160);
            }
        }

        private static void VerifyRows(LiteDatabase db, bool followerAcknowledged)
        {
            var second = db.GetCollection("docs").FindById(2)["value"].AsInt32;
            if (followerAcknowledged) second.Should().Be(2);
            else second.Should().BeOneOf(0, 2); // failed completion has an unknown, whole-transaction outcome
            var expected = Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? 1 : id == 2 ? second : 0)).ToArray();
            db.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
            foreach (var value in new[] { 0, 1, 2 })
                db.GetCollection("docs").Find(Query.EQ("value", value)).Should().BeEquivalentTo(expected.Where(row => row["value"] == value));
            foreach (var row in expected) Assert.Equal<BsonValue>(row, db.GetCollection("docs").FindById(row["_id"]));
            db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
        }

        private static Exception Finish(Task<Exception> task)
        {
            task.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
            return task.GetAwaiter().GetResult();
        }

        private static Task<Exception> Start(Action action, Action<string> observer) => Task.Factory.StartNew(() =>
        {
            SharedMemoryReaderTable.PublicationStage = observer;
            try { action(); return null; }
            catch (Exception error) { return error; }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
#endif
