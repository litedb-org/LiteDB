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
using static LiteDB.Tests.Internals.DirectHostGroupCommit_Tests;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitUnsupported_Tests
    {
        [Theory]
        [InlineData(0, "wal-after-index-confirmation", false)]
        [InlineData(22, "wal-after-index-confirmation", false)]
        [InlineData(30, "wal-after-index-confirmation", false)]
        [InlineData(0, "wal-before-ticket-completion", false)]
        [InlineData(22, "wal-before-ticket-completion", false)]
        [InlineData(22, "wal-before-ticket-completion", true)]
        public async Task Downgrade_completes_issued_tickets_and_exposes_every_acknowledged_commit(int errno, string pause, bool laterWriteFirst)
        {
            using var scenario = new DowngradeScenario(pause, errno);
            try
            {
                await scenario.Downgrade();
                scenario.Visible(0, 1);
                if (laterWriteFirst)
                {
                    scenario.Facades[2].GetCollection("rows").Insert(Row(3));
                    scenario.Visible(0, 1, 2, 3);
                }
                scenario.ReleaseFollower.Set();
                await Completes(scenario.Follower);
                scenario.Follower.Result.Should().BeNull();
                scenario.Visible(laterWriteFirst ? new[] { 0, 1, 2, 3 } : new[] { 0, 1, 2 });
                scenario.Group.PendingVersions.Should().BeEmpty();
                scenario.Disk.CommitReadVersionLimit.Should().Be(int.MaxValue);

                if (!laterWriteFirst) scenario.Facades[2].GetCollection("rows").Insert(Row(3));
                scenario.Visible(0, 1, 2, 3);
                scenario.Syncs.Should().BeGreaterThanOrEqualTo(2, "the follower must complete a second covered flush");
                scenario.VerifySavedFiles(0, 1, 2, 3);
                scenario.CheckpointAndVerify(0, 1, 2, 3);
                scenario.Group.PendingVersions.Should().BeEmpty();
            }
            finally { await scenario.Finish(); }
            scenario.CloseAndVerify(0, 1, 2, 3);
        }

        [Theory]
        [InlineData("noop")]
        [InlineData("explicit-noop")]
        [InlineData("unique-error")]
        [InlineData("explicit-unique-error")]
        [InlineData("index-error")]
        [InlineData("for-update")]
        public async Task Zero_frame_results_errors_and_queries_complete_dependencies_after_downgrade(string operation)
        {
            using var scenario = new DowngradeScenario("wal-before-ticket-completion", 22);
            try
            {
                await scenario.Downgrade();
                scenario.Visible(0, 1);
                var before = TempFile.ReadAllBytesShared(scenario.LogPath).Length;
                RunDependent(scenario, operation);
                scenario.Syncs.Should().Be(2, "a dependency must flush even when grouping is no longer eligible");
                TempFile.ReadAllBytesShared(scenario.LogPath).Length.Should().Be(before, "the dependent wrote no frames");
                scenario.Visible(0, 1, 2);
                scenario.Group.PendingVersions.Should().BeEmpty();
                scenario.Disk.CommitReadVersionLimit.Should().Be(int.MaxValue);
                scenario.VerifySavedFiles(0, 1, 2);
                scenario.ReleaseFollower.Set();
                await Completes(scenario.Follower);
                scenario.Follower.Result.Should().BeNull();
                scenario.Syncs.Should().Be(2, "the follower's ticket was already covered");
                scenario.Visible(0, 1, 2);
            }
            finally { await scenario.Finish(); }
            scenario.CloseAndVerify(0, 1, 2);
        }

        [Theory]
        [InlineData("wal-before-batch-flush")]
        [InlineData("device")]
        public async Task Failed_completion_after_downgrade_stops_all_facades_without_acknowledging_the_follower(string failure)
        {
            using var scenario = new DowngradeScenario("wal-before-ticket-completion", 22, failure);
            try
            {
                await scenario.Downgrade();
                scenario.Visible(0, 1);
                Action dependent = () => scenario.Facades[2].GetCollection("rows").Delete(99);
                dependent.Should().Throw<IOException>();
                scenario.ReleaseFollower.Set();
                await Completes(scenario.Follower);
                scenario.Follower.Result.Should().BeAssignableTo<IOException>();
                foreach (var facade in scenario.Facades)
                {
                    Action read = () => facade.GetCollection("rows").Count();
                    read.Should().Throw<IOException>();
                }
                scenario.Syncs.Should().Be(failure == "device" ? 2 : 1);
            }
            finally { await scenario.Finish(); }
            foreach (var engine in scenario.Engines) engine.Dispose();
            VerifyRecovery(scenario.Filename, new[] { 0, 1 });
        }

        private static void RunDependent(DowngradeScenario scenario, string operation)
        {
            var db = scenario.Facades[2];
            var rows = db.GetCollection("rows");
            switch (operation)
            {
                case "noop": rows.Delete(99).Should().BeFalse(); break;
                case "explicit-noop":
                    db.BeginTrans().Should().BeTrue();
                    rows.Delete(99).Should().BeFalse();
                    db.Commit().Should().BeTrue();
                    break;
                case "explicit-unique-error":
                    db.BeginTrans().Should().BeTrue();
                    goto case "unique-error";
                case "unique-error":
                    Action insert = () => rows.Insert(Row(2));
                    insert.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                    break;
                case "index-error":
                    Action index = () => rows.EnsureIndex("value", "$.other");
                    index.Should().Throw<LiteException>();
                    break;
                case "for-update":
                    using (var reader = scenario.Engines[2].Query("rows", new Query { ForUpdate = true }))
                    {
                        var count = 0;
                        while (reader.Read()) count++;
                        count.Should().Be(3);
                    }
                    break;
                default: throw new ArgumentException(operation);
            }
        }

        private sealed class DowngradeScenario : IDisposable
        {
            private readonly TempFile _file = new TempFile();
            private readonly ManualResetEventSlim _leaderAtSync = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _releaseLeader = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _followerPaused = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim ReleaseFollower = new ManualResetEventSlim();
            internal readonly LiteEngine[] Engines;
            internal readonly LiteDatabase[] Facades;
            internal readonly DiskService Disk;
            internal readonly WalCommitGroup Group;
            internal Task<Exception> Follower;
            private readonly Task<Exception> _leader;
            private int _followerThread;
            private int _syncs;
            private readonly int _errno;
            internal string Filename => _file.Filename;
            internal string LogPath => NativeFileSyncCollection.LogPath(Filename);
            internal int Syncs => Volatile.Read(ref _syncs);

            internal DowngradeScenario(string pause, int errno, string failure = null)
            {
                _errno = errno;
                Seed(Filename);
                Engines = Enumerable.Range(0, 3).Select(_ => new LiteEngine(new EngineSettings
                    { Filename = Filename, CompactStorage = CompactStorageMode.Legacy })).ToArray();
                Facades = Engines.Select(engine => new LiteDatabase(engine, disposeOnClose: false)).ToArray();
                Facades[0].CheckpointSize = 0;
                Disk = (DiskService)typeof(LiteEngine).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Engines[0]);
                Group = Disk.CommitGroup;
                Group.Should().NotBeNull();
                EngineState.SimulateProcessCrash = point =>
                {
                    if (point == pause && Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _followerThread))
                    {
                        _followerPaused.Set();
                        Wait(ReleaseFollower);
                    }
                    if (failure == point && point == "wal-before-batch-flush" && Syncs == 1)
                        throw new IOException("Injected degraded completion flush failure");
                };
                NativeFileSync.SimulateErrno = path =>
                {
                    if (path != LogPath) return 0;
                    var number = Interlocked.Increment(ref _syncs);
                    if (number == 1) { _leaderAtSync.Set(); Wait(_releaseLeader); return errno; }
                    return failure == "device" ? 5 : errno;
                };
                _leader = Start(() => Facades[0].GetCollection("rows").Insert(Row(1)));
            }

            internal async Task Downgrade()
            {
                Wait(_leaderAtSync);
                Follower = Start(() =>
                {
                    Volatile.Write(ref _followerThread, Thread.CurrentThread.ManagedThreadId);
                    Facades[1].GetCollection("rows").Insert(Row(2));
                });
                Wait(_followerPaused);
                Visible(0);
                _releaseLeader.Set();
                await Completes(_leader);
                _leader.Result.Should().BeNull();
                foreach (var db in Facades)
                    db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().Be(_errno == 0);
                (Disk.CommitGroup != null).Should().Be(_errno == 0);
                Group.PendingVersions.Should().NotBeEmpty();
                Follower.IsCompleted.Should().BeFalse();
            }

            internal void Visible(params int[] expected)
            {
                foreach (var db in Facades) Verify(db, expected);
            }

            private static void Verify(LiteDatabase db, int[] expected)
            {
                var rows = db.GetCollection("rows");
                rows.FindAll().Select(row => row["_id"].AsInt32).Should().BeEquivalentTo(expected);
                foreach (var id in Enumerable.Range(0, 5))
                {
                    var row = rows.FindById(id);
                    if (expected.Contains(id)) Assert.Equal<BsonValue>(Row(id), row);
                    else Assert.Null(row);
                    rows.Find(Query.EQ("value", id)).Select(item => item["_id"].AsInt32)
                        .Should().BeEquivalentTo(expected.Where(value => value == id));
                }
            }

            internal void VerifySavedFiles(params int[] expected)
            {
                // Copy before disposal/checkpoint: restart from the OS-cache image,
                // rather than allowing graceful close to repair visibility or flush it.
                using var copy = new TempFile();
                File.WriteAllBytes(copy.Filename, TempFile.ReadAllBytesShared(Filename));
                File.WriteAllBytes(FileHelper.GetLogFile(copy.Filename), TempFile.ReadAllBytesShared(LogPath));
                for (var open = 0; open < 2; open++)
                {
                    using var recovered = new LiteDatabase(copy.Filename);
                    Verify(recovered, expected);
                    recovered.Checkpoint();
                }
            }

            internal async Task Finish()
            {
                _releaseLeader.Set();
                ReleaseFollower.Set();
                try { await Completes(Task.WhenAll(new[] { _leader, Follower }.Where(task => task != null))); }
                finally { EngineState.SimulateProcessCrash = null; NativeFileSync.SimulateErrno = null; }
            }

            internal void CloseAndVerify(params int[] expected)
            {
                foreach (var engine in Engines) engine.Dispose();
                for (var open = 0; open < 2; open++)
                {
                    using var reopened = new LiteDatabase(Filename);
                    Verify(reopened, expected);
                    reopened.Checkpoint();
                }
            }

            internal void CheckpointAndVerify(params int[] expected)
            {
                // Public maintenance requires exclusive facade ownership.
                Engines[0].Dispose();
                Engines[1].Dispose();
                Facades[2].Checkpoint();
                Verify(Facades[2], expected);
            }

            public void Dispose()
            {
                foreach (var facade in Facades) facade.Dispose();
                foreach (var engine in Engines) engine.Dispose();
                _leaderAtSync.Dispose();
                _releaseLeader.Dispose();
                _followerPaused.Dispose();
                ReleaseFollower.Dispose();
                _file.Dispose();
            }
        }
    }
}
#endif
