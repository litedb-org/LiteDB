#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 13 of docs/decisions/durability-policy.md: when a commit's log sync fails after its
    /// frames reached the operating system, its caller gets "outcome unknown", and the read-only
    /// engine that replaces the failed one replays the WAL only up to the last commit acknowledged
    /// before it: this process never shows a transaction its caller saw fail. A later open lets the
    /// files decide. If anything committed on top of the failed batch meanwhile, the files win.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class AcknowledgedReopen_Tests
    {
        private const int EIO = 5;

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Reopen_after_a_failed_log_sync_shows_only_acknowledged_commits(string password)
        {
            using var file = new TempFile();
            var connection = password == null ? file.Filename : $"Filename={file.Filename};Password={password}";
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    db.CheckpointSize = 0;
                    var rows = db.GetCollection("rows");
                    rows.Insert(new[] { Row(1), Row(2) });
                    rows.Insert(Row(3));
                    fail = true;
                    Action insert = () => rows.Insert(Row(4));
                    insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("Unknown",
                        "the commit's frames reached the operating system before its sync failed");
                    fail = false;

                    Ids(db).Should().Equal(new[] { 1, 2, 3 }, "the caller saw commit 4 fail");
                    var info = db.Execute("SELECT $ FROM $database").Single();
                    info["readOnly"].AsBoolean.Should().BeTrue();
                    info["writeFailure"]["operation"].AsString.Should().Be("A commit's log flush");
                }
                using var later = new LiteDatabase(connection);
                Ids(later).Should().Equal(new[] { 1, 2, 3, 4 }, "a later open lets the files decide: commit 4's frames reached them");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        [Fact]
        public void Commits_of_another_connection_on_top_of_the_failed_batch_let_the_files_win()
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using var a = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                using var b = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                a.CheckpointSize = 0;
                b.CheckpointSize = 0;
                a.GetCollection("rows").Insert(Row(1));
                fail = true;
                Action insert = () => a.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>();
                fail = false;
                Ids(a).Should().Equal(new[] { 1 }, "the caller saw commit 2 fail");

                // Another connection recovers the WAL as the files hold it, and commits on top of it.
                b.GetCollection("rows").Insert(Row(3));
                Ids(b).Should().Equal(1, 2, 3);
                Ids(a).Should().Equal(new[] { 1, 2, 3 }, "the WAL grew past the failed batch: the files win");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        /// <summary>
        /// Another engine (another connection or process) recovers the failed commit from the files,
        /// whole, and a partial checkpoint (a reader holds an older snapshot) copies its pages into the
        /// data file without changing the log's length or salt. The reopened engine then must not mix
        /// the data file's copy of the failed commit with a log bounded before it: its view is one
        /// state of the database, here the files'. Review finding B1.
        /// </summary>
        [Theory]
        [InlineData("y")] // the failed commit supersedes an acknowledged commit's page: nothing is retired
        [InlineData("z")] // it supersedes that commit's confirmation frame
        [InlineData(null)] // it touches no page that has a version in the WAL
        public void Reopen_after_another_engine_checkpointed_the_failed_commit_shows_one_state(string touched)
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using var a = new LiteDatabase(file.Filename);
                a.CheckpointSize = 0;
                a.GetCollection("x").Insert(new BsonDocument { ["_id"] = 1 });
                a.GetCollection("y").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "a" });
                a.GetCollection("z").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "a" });
                a.Checkpoint();
                a.BeginTrans();
                a.GetCollection("y").Update(new BsonDocument { ["_id"] = 1, ["v"] = "b" });
                a.GetCollection("z").Update(new BsonDocument { ["_id"] = 1, ["v"] = "b" });
                a.Commit();
                fail = true;
                a.BeginTrans();
                a.GetCollection("x").Insert(new BsonDocument { ["_id"] = 2 });
                if (touched != null) a.GetCollection(touched).Update(new BsonDocument { ["_id"] = 1, ["v"] = "c" });
                Action commit = () => a.Commit();
                commit.Should().Throw<IOException>();
                fail = false;

                using (var b = new LiteEngine(new EngineSettings { Filename = file.Filename, SharedReaderVersions = () => new[] { int.MaxValue } }))
                    b.Checkpoint();

                var x = a.GetCollection("x").FindAll().Select(d => d["_id"].AsInt32).OrderBy(i => i).ToArray();
                var value = a.GetCollection(touched ?? "y").FindById(1)["v"].AsString;
                x.Should().Equal(new[] { 1, 2 }, "another engine built on the failed commit: the files win, whole");
                value.Should().Be(touched == null ? "b" : "c");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        /// <summary>
        /// Another engine drains the WAL (a full checkpoint restarts it with a new salt), and in the
        /// second case commits on top of the new generation. The failed engine's record no longer
        /// describes the files: its reopen shows them as they are, whole, never the old bound over a
        /// new generation.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reopen_after_another_engine_restarted_the_wal_lets_the_files_win(bool commitsAfter)
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using var a = new LiteDatabase(file.Filename);
                a.CheckpointSize = 0;
                a.GetCollection("rows").Insert(Row(1));
                fail = true;
                Action insert = () => a.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("Unknown");
                fail = false;

                using (var b = new LiteDatabase(file.Filename))
                {
                    b.Checkpoint();
                    // Separate commits: the new generation grows past the end the failure recorded.
                    if (commitsAfter) foreach (var id in Enumerable.Range(3, 4)) b.GetCollection("rows").Insert(Row(id));
                }

                Ids(a).Should().Equal(commitsAfter ? new[] { 1, 2, 3, 4, 5, 6 } : new[] { 1, 2 },
                    "the WAL the failure left is gone: the files win, whole");
                a.Execute("SELECT $ FROM $database").Single()["readOnly"].AsBoolean.Should().BeTrue();
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        /// <summary>
        /// The files are replaced by another database (restored or copied over) before the failed
        /// engine's next call: its reopen reads the replacement as it is, not the old bound applied to it.
        /// </summary>
        [Fact]
        public void Reopen_after_the_files_were_replaced_reads_the_replacement()
        {
            using var file = new TempFile();
            using var other = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var otherLog = FileHelper.GetLogFile(other.Filename);
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using var a = new LiteDatabase(file.Filename);
                a.CheckpointSize = 0;
                a.GetCollection("rows").Insert(Row(1));
                fail = true;
                Action insert = () => a.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>();
                fail = false;

                // The replacement also keeps commits in its WAL.
                using (var replacement = new LiteDatabase(other.Filename))
                {
                    replacement.CheckpointSize = 0;
                    replacement.GetCollection("rows").Insert(new[] { Row(7), Row(8) });
                    replacement.GetCollection("rows").Insert(Row(9));
                }
                File.Copy(other.Filename, file.Filename, overwrite: true);
                if (File.Exists(otherLog)) File.Copy(otherLog, logName, overwrite: true);
                else File.Delete(logName);

                Ids(a).Should().Equal(new[] { 7, 8, 9 }, "the replacement is read as it is");
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                File.Delete(otherLog);
            }
        }

#if !NETFRAMEWORK
        /// <summary>
        /// Another process recovers the failed commit from the files and commits on top of it, while
        /// the shared connection whose commit failed keeps its failure (decision 6): its next operation
        /// opens read-only and shows the files, whole, as the other process left them.
        /// </summary>
        [Fact]
        public async Task Commits_of_another_process_on_top_of_the_failed_batch_let_the_files_win()
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var fail = false;
            NativeFileSync.SimulateErrno = path => fail && Path.GetFullPath(path) == logName ? EIO : 0;
            try
            {
                using var a = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                a.CheckpointSize = 0;
                a.GetCollection("docs").Insert(Row(1));
                fail = true;
                Action insert = () => a.GetCollection("docs").Insert(Row(2));
                insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("Unknown");
                fail = false;
                Ids(a, "docs").Should().Equal(new[] { 1 }, "the caller saw commit 2 fail");

                await MvccProcess.Run("insert", file.Filename, null, "100");

                Ids(a, "docs").Should().Equal(new[] { 1, 2 }.Concat(Enumerable.Range(100, 20)),
                    "the other process built on the failed commit: the files win, whole");
                var info = a.Execute("SELECT $ FROM $database").Single();
                info["readOnly"].AsBoolean.Should().BeTrue("the connection keeps its failure");
                Action again = () => a.GetCollection("docs").Insert(Row(3));
                again.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix);
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }
#endif

        /// <summary>A commit refused before its first frame is marked as not committed.</summary>
        [Fact]
        public void Commit_refused_before_it_writes_is_marked_not_committed()
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").Insert(Row(1));
            DurableLogs.Forget(logName);
            NativeFileSync.SimulateErrno = path => Path.GetFullPath(path) == logName ? 22 : 0;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action insert = () => db.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("NotCommitted");
            }
            finally { NativeFileSync.SimulateErrno = null; }
            using var reopened = new LiteDatabase(file.Filename);
            Ids(reopened).Should().Equal(1);
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };

        private static int[] Ids(LiteDatabase db, string collection = "rows") =>
            db.GetCollection(collection).FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
    }
}
#endif
