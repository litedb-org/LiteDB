#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision D and implementation note 12: opted out of durable commits, on a log that cannot sync,
    /// the WAL limit holds because no checkpoint can drain the WAL. An engine that had not itself seen
    /// a log barrier fail (a reopen, every operation of a shared connection) took the log as syncing:
    /// the next insert passed the limit and the WAL grew without bound, and $database.walKept read
    /// false. Such an engine now tries a log sync where it needs to know (independent review B).
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class FreshEngineLogBarrier_Tests
    {
        private const long Limit = 64 * 1024;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Wal_limit_on_an_unsyncable_log_holds_for_a_fresh_engine(bool shared)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").EnsureIndex("value");
            using var power = new FilePowerLossModel(file.Filename) { LogFails = true };
            var connection = $"Filename={file.Filename};durable commits=false;wal limit=64KB";
            try
            {
                var rows = 0;
                // The engine that finds out: the limit holds there (OverwriteBarrier_Tests).
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, DurableCommits = false, WalLimit = Limit }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("rows").Insert(Row(++rows));
                    engine.Checkpoint().Should().Be(0);
                    while (Info(db)["logFileSize"].AsInt64 <= Limit) db.GetCollection("rows").Insert(Row(++rows));
                    Action refused = () => db.GetCollection("rows").Insert(Row(rows + 1));
                    refused.Should().Throw<IOException>().WithMessage("*passed the WAL limit*");
                }
                new FileInfo(logName).Length.Should().BeGreaterThan(Limit, "the WAL was kept");

                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                using (var db = new LiteDatabase(connection + (shared ? ";connection=shared" : "")))
                {
                    if (shared) db.Checkpoint(); // a checkpoint in this connection finds out again
                    Action insert = () => db.GetCollection("rows").Insert(Row(rows + 1));
                    insert.Should().Throw<IOException>().WithMessage("*passed the WAL limit*while the log file cannot sync*", "the log still cannot sync");
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1, "a refused write changes nothing");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);
                    Info(db)["walKept"].AsBoolean.Should().BeTrue("no checkpoint can drain the WAL while the log cannot sync");
                    Info(db)["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                    db.GetCollection("rows").Count().Should().Be(rows, "reads keep working");
                }

                power.LogFails = false;
                using (var db = new LiteDatabase(connection + (shared ? ";connection=shared" : "")))
                {
                    db.GetCollection("rows").Insert(Row(++rows)); // the log syncs again: writes resume
                    Info(db)["walKept"].AsBoolean.Should().BeFalse();
                    db.Checkpoint();
                    db.GetCollection("rows").Count().Should().Be(rows);
                }
            }
            finally { File.Delete(logName); }
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 5, ["payload"] = new string((char)('a' + id % 26), 1500) + id
        };

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();
    }
}
#endif
