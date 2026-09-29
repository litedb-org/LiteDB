#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 6 of docs/decisions/durability-policy.md (a real write or sync failure is sticky: it is
    /// recorded, the engine reopens read-only on its next call, reads keep working, and every later
    /// write throws with the record). Refusals before anything was written (the data file cannot sync,
    /// implementation note 6) are no failure.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public partial class StickyFailure_Tests
    {
        private const int Rows = 16;
        private const int EIO = 5, EINVAL = 22;

        /// <summary>
        /// Past the WAL limit a write first retries the data sync. When that sync failed (an I/O error,
        /// not "cannot sync") the write threw it, but nothing was recorded and the engine kept writing.
        /// It is now a sticky failure: recorded on the data file, the engine continues read-only with
        /// every row, and refuses writes with it.
        /// </summary>
        [Fact]
        public void Data_sync_that_fails_at_the_wal_limit_is_recorded_and_the_engine_continues_read_only()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var connection = $"Filename={file.Filename};wal limit=1MB";
            using (var setup = new LiteDatabase(connection)) setup.GetCollection("rows").EnsureIndex("value"); // its header synced
            using var syncs = new Syncs(file.Filename) { Data = EINVAL };
            try
            {
                using var db = new LiteDatabase(connection);
                var count = 0;
                while (Info(db)["logFileSize"].AsInt64 <= 1024 * 1024) db.GetCollection("rows").Insert(Row(++count));
                syncs.Data = EIO;
                Action insert = () => db.GetCollection("rows").Insert(Row(count + 1));
                var failure = insert.Should().Throw<IOException>().Which;
                failure.Message.Should().NotStartWith("Cannot modify", "the data sync failed, it did not answer \"cannot sync\"");

                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                db.GetCollection("rows").Count().Should().Be(count);
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A data sync", "data", failure.Message, walKept: true);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(insert, record).InnerException.Should().BeSameAs(failure);
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1);
                SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);
                db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, count));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// $database.walKept retries the data sync of a writable engine that has not seen one succeed.
        /// When that sync failed with an I/O error, the $database read threw it (decision 2: reads keep
        /// working). The read now reports it (readOnly, writeFailure on the data file, walKept), the next
        /// call reopens the engine read-only, and writes throw it.
        /// </summary>
        [Fact]
        public void Database_read_whose_data_sync_fails_reports_the_failure_instead_of_throwing_it()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var syncs = new Syncs(file.Filename);
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                syncs.Data = EINVAL;
                Update(db, 1); // durable in the WAL
                Info(db)["walKept"].AsBoolean.Should().BeTrue("the data file cannot sync");
                syncs.Data = EIO;

                BsonDocument info = null;
                Action read = () => info = Info(db);
                read.Should().NotThrow("a $database read does not fail for a failed data sync");
                info["walKept"].AsBoolean.Should().BeTrue();
                info["readOnly"].AsBoolean.Should().BeTrue();
                var failure = info["writeFailure"].AsDocument;
                failure["operation"].AsString.Should().Be("A data sync");
                failure["file"].AsString.Should().Be("data");
                info["readOnlyReason"].AsString.Should().StartWith("A data sync failed at");

                // The read could not stop the engine; its stop is due, and the next call makes it.
                var failed = engine.GetState();
                failed.StopDue.Should().BeTrue();
                failed.Stopped.Should().BeFalse();
                SyncPowerLossModel.AssertRows(db, Rows, 1);
                failed.Stopped.Should().BeTrue("the next call stopped the engine whose failure the read recorded");
                engine.GetState().Should().NotBeSameAs(failed, "and reopened it read-only");
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A data sync", "data", failure["error"].AsString, walKept: true);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => Update(db, 2), record);
                SyncPowerLossModel.AssertRows(db, Rows, 1);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Implementation note 6: a retiring checkpoint whose promotion is refused before it wrote
        /// anything (the data file cannot sync) is no failure. When an automatic checkpoint after a
        /// commit hit it, the refusal was recorded and the engine went read-only; recorded no longer, it
        /// must not throw from that commit either (decision 5). Writes stay durable in the WAL.
        /// </summary>
        [Fact]
        public void Automatic_checkpoint_refused_before_it_wrote_is_neither_thrown_nor_recorded()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename);
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(db, value, 64);
                Exception thrown = null;
                using (var reader = engine.Query("rows", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    var thread = new Thread(() => thrown = Record.Exception(() =>
                    {
                        for (var value = 6; value <= 9; value++) Update(db, value, 64);
                        var data = SyncPowerLossModel.ReadShared(file.Filename);
                        power.DataFailsFromSync = power.DataSyncs + 2; // the retirement proof's data sync, then the promotion's
                        db.CheckpointSize = 1; // this pragma's commit runs a retiring checkpoint: the reader keeps the WAL
                        power.DataSyncs.Should().BeGreaterOrEqualTo(power.DataFailsFromSync, "the promotion tried its data sync");
                        SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the refused promotion wrote nothing");
                    }));
                    thread.Start();
                    thread.Join();
                }
                thrown.Should().BeNull("the commit succeeded, and its checkpoint's refusal is no failure");
                Info(db)["writeFailure"].IsNull.Should().BeTrue();
                Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                Update(db, 10, 64);
                Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                SyncPowerLossModel.AssertRows(db, 64, 10);
                power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, 64, 10));
            }
            finally { File.Delete(logName); }
        }

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value, int rows = Rows) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 5, ["payload"] = new string((char)('a' + id % 26), 4000) + id
        };

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        /// <summary>The errno each file's syncs answer (0: they succeed): EINVAL is "cannot sync" (#2242), EIO a failed sync.</summary>
        private sealed class Syncs : IDisposable
        {
            private readonly string _data, _log;
            internal volatile int Data, Log;

            internal Syncs(string filename)
            {
                _data = Path.GetFullPath(filename);
                _log = Path.GetFullPath(FileHelper.GetLogFile(filename));
                NativeFileSync.SimulateErrno = path =>
                {
                    var name = Path.GetFullPath(path);
                    if (string.Equals(name, _data, StringComparison.OrdinalIgnoreCase)) return Data;
                    if (string.Equals(name, _log, StringComparison.OrdinalIgnoreCase)) return Log;
                    return 0;
                };
            }

            public void Dispose() => NativeFileSync.SimulateErrno = null;
        }
    }
}
#endif
