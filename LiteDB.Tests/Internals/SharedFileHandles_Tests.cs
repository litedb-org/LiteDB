using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>
    /// Shared connections keep their data/log handles open between operations (Windows).
    /// Every operation still rebuilds its engine state from the files, so each test
    /// compares what a connection with cached handles sees against a fresh connection.
    /// </summary>
    public class SharedFileHandles_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-handles-" + Guid.NewGuid().ToString("N"));

        public SharedFileHandles_Tests() => Directory.CreateDirectory(_directory);

        private string Filename => Path.Combine(_directory, "test.db");

        private string LogFilename => FileHelper.GetLogFile(this.Filename);

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private SharedEngine OpenShared(string password = null) =>
            new SharedEngine(new EngineSettings { Filename = this.Filename, Password = password });

        [Fact]
        public void Ntfs_volumes_report_posix_delete_and_keep_the_cache()
        {
            if (!IsWindows) return;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(this.Filename)));
            if (drive.DriveFormat != "NTFS") return;
            SharedFileHandles.IsSupportedFor(this.Filename).Should().BeTrue(
                "NTFS reports FILE_SUPPORTS_POSIX_UNLINK_RENAME, so a deleted WAL frees its name at once");
            using var engine = this.OpenShared();
            engine.FileHandles.Should().NotBeNull();
        }

#if DEBUG || TESTING
        [Fact]
        public void Volumes_without_posix_delete_open_files_per_operation()
        {
            // Only this test's files: connections of tests running in parallel keep the real answer.
            var directory = _directory;
            SharedFileHandles.SimulatePosixDelete = path =>
                path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? false : (bool?)null;
            try
            {
                using var first = this.OpenShared();
                using var second = this.OpenShared();
                first.FileHandles.Should().BeNull("a pending-delete WAL name would block the peers' next open");
                using (var db = new LiteDatabase(first, disposeOnClose: false))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('x', 2000) });
                }
                using (var db = new LiteDatabase(second, disposeOnClose: false))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                    db.Checkpoint();
                    db.GetCollection("rows").Count().Should().Be(2);
                }
                using (var db = new LiteDatabase(first, disposeOnClose: false))
                {
                    db.GetCollection("rows").Count().Should().Be(2);
                }
            }
            finally
            {
                SharedFileHandles.SimulatePosixDelete = null;
            }
        }
#endif

        [Fact]
        public void Operations_reuse_the_open_handles()
        {
            if (!IsWindows) return;
            using var engine = this.OpenShared();
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            // Warm up: the first writes and reads open each file's writer and reader.
            rows.Insert(new BsonDocument { ["_id"] = 0 });
            (rows.FindById(0) != null).Should().BeTrue();
            rows.Count().Should().Be(1);
            var opened = engine.FileHandles.Opened;

            // Stay below the close-checkpoint threshold: deleting the WAL would reopen its handles.
            for (var id = 1; id <= 10; id++)
            {
                rows.Insert(new BsonDocument { ["_id"] = id });
                (rows.FindById(id) != null).Should().BeTrue();
            }

            engine.FileHandles.Opened.Should().Be(opened, "later operations reuse the handles of the first");
            engine.FileHandles.Reused.Should().BeGreaterOrEqualTo(20);
            engine.FileHandles.Invalidated.Should().Be(0);
            rows.Count().Should().Be(11);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Changes_by_another_connection_are_visible_after_its_checkpoint_replaced_the_wal(string password)
        {
            if (!IsWindows) return;
            using var engine = this.OpenShared(password);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 0, ["payload"] = new string('x', 2000) });
            rows.Count().Should().Be(1, "this connection now holds data and log handles");
            var expected = 1;

            for (var round = 1; round <= 3; round++)
            {
                // The other connection grows the data file, writes a WAL and, on dispose,
                // checkpoints and deletes it: this connection's log handle names a deleted file.
                using (var other = this.OpenShared(password))
                using (var otherDb = new LiteDatabase(other, disposeOnClose: false))
                {
                    otherDb.GetCollection("rows").InsertBulk(Enumerable.Range(round * 1000, 200)
                        .Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('y', 2000) }));
                }
                File.Exists(this.LogFilename).Should().BeFalse("the other connection's final close checkpointed the WAL");

                expected += 200;
                rows.Count().Should().Be(expected);
                rows.Insert(new BsonDocument { ["_id"] = -round });
                rows.Count().Should().Be(++expected);
                this.AssertSameAsFreshConnection(rows, password);
            }
            engine.FileHandles.Invalidated.Should().BeGreaterThan(0, "the deleted WAL's handle was detected and closed");
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Commits_and_checkpoints_by_other_processes_are_visible()
        {
            if (!IsWindows) return;
            await MvccProcess.Run("seed", this.Filename, null);
            using var engine = new SharedEngine(new EngineSettings { Filename = this.Filename, TransactionPageLimit = 1 });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var docs = db.GetCollection("docs");
            docs.FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 0);

            await MvccProcess.Run("history", this.Filename, null);
            docs.FindAll().Should().HaveCount(64).And.OnlyContain(doc => doc["value"].AsInt32 == 20);

            await MvccProcess.Run("write", this.Filename, null, "33");
            docs.FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 33);

            await MvccProcess.Run("checkpoint", this.Filename, null);
            docs.Update(Enumerable.Range(0, 64).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = 44, ["payload"] = new string('x', 3000) }));

            using var reader = new MvccProcess("read", this.Filename, null);
            await reader.Expect("value:44");
            await reader.Finish();
        }

        [Fact]
        public async Task A_killed_process_holding_cached_handles_does_not_block_its_peers()
        {
            if (!IsWindows) return;
            await MvccProcess.Run("seed", this.Filename, null);
            using (var holder = new MvccProcess("hold", this.Filename, null))
            {
                await holder.Expect("ready");
                await holder.Kill();
            }

            using (var engine = this.OpenShared())
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Update(Enumerable.Range(0, 64).Select(id =>
                    new BsonDocument { ["_id"] = id, ["value"] = 7, ["payload"] = new string('x', 3000) }));
                db.Checkpoint();
            }
            File.Exists(this.LogFilename).Should().BeFalse("no dead process keeps the WAL from being deleted");
            using var reader = new MvccProcess("read", this.Filename, null);
            await reader.Expect("value:7");
            await reader.Finish();
        }
#endif

        [Fact]
        public void Rebuild_by_another_connection_replaces_the_files_behind_cached_handles()
        {
            if (!IsWindows) return;
            using var engine = this.OpenShared();
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 300).Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 500) }));
            rows.DeleteMany(Query.GT("_id", 100));
            rows.Count().Should().Be(100);

            using (var other = this.OpenShared())
            using (var otherDb = new LiteDatabase(other, disposeOnClose: false))
            {
                otherDb.Rebuild();
                otherDb.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1000 });
            }

            rows.Count().Should().Be(101);
            rows.Insert(new BsonDocument { ["_id"] = 1001 });
            this.AssertSameAsFreshConnection(rows, null);
            engine.FileHandles.Invalidated.Should().BeGreaterThan(0, "the rebuilt data file is a different file");
        }

        [Fact]
        public void Direct_mode_is_refused_while_a_shared_connection_is_open()
        {
            if (!IsWindows) return;
            var engine = this.OpenShared();
            var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });

            // Between operations, not only during one: the shared connection keeps a
            // writable handle, which a direct writer (FileShare.Read) cannot coexist with.
            Action direct = () => new LiteDatabase(this.Filename).Dispose();
            direct.Should().Throw<IOException>();
            Action exclusiveRead = () => new FileStream(this.Filename, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose();
            exclusiveRead.Should().Throw<IOException>("a reader that denies write sharing cannot open a file in use");
            // File.Copy shares write access with the source, so file copies keep working.
            File.Copy(this.Filename, this.Filename + ".copy");
            new FileInfo(this.Filename + ".copy").Length.Should().Be(new FileInfo(this.Filename).Length);

            db.Dispose();
            engine.Dispose();
            using var reopened = new LiteDatabase(this.Filename);
            reopened.GetCollection("rows").Count().Should().Be(1);
        }

        private void AssertSameAsFreshConnection(ILiteCollection<BsonDocument> rows, string password)
        {
            var cached = rows.FindAll().Select(doc => doc["_id"].AsInt32).OrderBy(id => id).ToArray();
            using var fresh = this.OpenShared(password);
            using var freshDb = new LiteDatabase(fresh, disposeOnClose: false);
            freshDb.GetCollection("rows").FindAll().Select(doc => doc["_id"].AsInt32).OrderBy(id => id)
                .Should().Equal(cached);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
