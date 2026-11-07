using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class PrimaryKeyDeleteFault_Tests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData(null, false)]
        [InlineData("secret", true)]
        [InlineData("secret", false)]
        public void Every_delete_wal_write_failure_recovers_atomic_rows_and_indexes(string password, bool durable)
        {
            var writes = Exercise(password, 0, durable);
            writes.Should().BeGreaterThan(4, "the delete must cross safepoints and index/data pages");
            for (var fault = 1; fault <= writes; fault++) Exercise(password, fault, durable);
        }

        private static int Exercise(string password, int failAt, bool durable)
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, Password = password, DurableCommits = durable, TransactionPageLimit = 4 };
            byte[] data = null, wal = null;
            var writes = 0;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Insert(Enumerable.Range(1, 96).Select(n => PrimaryKeyDeleteRange_Tests.Document(n, 0)));
                db.GetCollection("docs").EnsureIndex("n");
                db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "safe" });
                db.Checkpoint();
                engine.SimulateDiskWriteFail = page =>
                {
                    if (++writes != failAt) return;
                    // Capture before exception cleanup or engine disposal can repair the files.
                    data = TempFile.ReadAllBytesShared(file.Filename);
                    wal = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
                    throw new IOException("delete WAL fault");
                };
                if (failAt == 0) db.GetCollection("docs").DeleteMany("_id <= 80").Should().Be(80);
                else
                {
                    Action delete = () => db.GetCollection("docs").DeleteMany("_id <= 80");
                    delete.Should().Throw<IOException>().WithMessage("delete WAL fault");
                    data.Should().NotBeNull();
                }
                engine.SimulateDiskWriteFail = null;
            }
            if (failAt != 0)
            {
                using var image = new TempFile();
                File.WriteAllBytes(image.Filename, data);
                File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), wal);
                Recover(image.Filename, password, false);
            }
            else Recover(file.Filename, password, true);
            return writes;
        }

        [Theory]
        [InlineData("wal-confirmation-before-write", true)]
        [InlineData("wal-confirmation-before-write", false)]
        [InlineData("wal-confirmation-after-write", true)]
        [InlineData("wal-confirmation-after-write", false)]
        public void Interrupted_confirmation_recovers_whole_delete_on_repeated_reopen(string phase, bool durable)
        {
            using var file = new TempFile();
            byte[] data = null, wal = null;
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, DurableCommits = durable, TransactionPageLimit = 4 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Insert(Enumerable.Range(1, 96).Select(n => PrimaryKeyDeleteRange_Tests.Document(n, 0)));
                db.GetCollection("docs").EnsureIndex("n");
                db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "safe" });
                db.Checkpoint();
                var state = (EngineState)typeof(LiteEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                state.AtCrashPoint = current =>
                {
                    if (current != phase) return;
                    data = TempFile.ReadAllBytesShared(file.Filename);
                    wal = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
                    throw new IOException("confirmation cut");
                };
                try
                {
                    Action delete = () => db.GetCollection("docs").DeleteMany("_id <= 80");
                    delete.Should().Throw<IOException>().WithMessage("confirmation cut");
                }
                finally { state.AtCrashPoint = null; }
            }
            data.Should().NotBeNull(); wal.Should().NotBeNull();
            using var image = new TempFile();
            File.WriteAllBytes(image.Filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), wal);
            Recover(image.Filename, null, phase == "wal-confirmation-after-write");
        }

        private static void Recover(string filename, string password, bool deleted)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
                PrimaryKeyDeleteRange_Tests.AssertRows(db, Enumerable.Range(deleted ? 81 : 1, deleted ? 16 : 96), 0);
                db.Checkpoint();
            }
        }
    }
}
