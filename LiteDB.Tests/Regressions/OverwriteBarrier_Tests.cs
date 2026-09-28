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
    /// No in-place overwrite of the data file behind a recovery copy that is only in the OS cache
    /// (decision D of docs/decisions/durability-policy.md; external review, point 1). A checkpoint's
    /// backfill, a format promotion, a conversion and the invalid-state mark overwrite the data file
    /// behind the header journal and the WAL, so they need both on the device first, also with
    /// "durable commits=false": opting out gives up recent commits, never the data file's integrity.
    /// When the log answers "cannot sync" (#2242) they refuse before the journal is written. Before,
    /// with durable commits off, they went ahead in write order, and a power loss mid-overwrite tore
    /// the data file. Opted out, "cannot sync" is still not a failure (proposed default A): a
    /// checkpoint writes nothing and keeps the WAL, quietly (decision 5); a promotion is refused
    /// with both files unchanged; and since no checkpoint can drain the WAL, the WAL limit holds.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class OverwriteBarrier_Tests
    {
        /// <summary>
        /// Opted out, on a log that cannot sync beside a data file that syncs: commits keep going to the
        /// WAL, and the automatic checkpoints after them, an explicit one and the close one write nothing
        /// to the data file and keep every WAL frame. Every row reads, through the index too; nothing is
        /// recorded as a failure, and durableLogFlush says commits are not durable. A power loss keeps
        /// the data file whole, with the rows it held when the log stopped syncing. Once the log syncs,
        /// a checkpoint drains the WAL into the data file.
        /// </summary>
        [Fact]
        public void Checkpoints_on_a_log_that_cannot_sync_write_nothing_and_keep_the_wal_without_durable_commits()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            Setup(file.Filename, 10);
            using var power = new FilePowerLossModel(file.Filename) { LogFails = true };
            var checkpoints = 0;
            var settings = new EngineSettings
            {
                Filename = file.Filename, DurableCommits = false,
                CheckpointStage = stage => { if (stage == "before-commit-lock") checkpoints++; }
            };
            try
            {
                var data = SyncPowerLossModel.ReadShared(file.Filename);
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 4; // pages: the commits below start automatic checkpoints
                    for (var id = 11; id <= 30; id++)
                    {
                        var kept = Frames(SyncPowerLossModel.ReadShared(logName));
                        db.GetCollection("rows").Insert(Row(id));
                        SyncPowerLossModel.ReadShared(logName).Take(kept.Length).Should().Equal(kept, "the WAL grows behind the frames it keeps");
                    }
                    checkpoints.Should().BeGreaterThan(10, "each commit past the checkpoint size started an automatic checkpoint");
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "no automatic checkpoint wrote to the data file");

                    var wal = SyncPowerLossModel.ReadShared(logName);
                    engine.Checkpoint().Should().Be(0, "a log that cannot sync backs no overwrite");
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "nor did the explicit one");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(wal, "the WAL is kept, without a journal footer");

                    AssertRows(db, 30);
                    var info = Info(db);
                    info["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                    info["readOnly"].AsBoolean.Should().BeFalse();
                    info["durableLogFlush"].AsBoolean.Should().BeFalse();
                    power.AfterPowerLoss(x => AssertRows(x, 10));
                    FilePowerLossModel.Open((SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName)), x => AssertRows(x, 30));
                    checkpoints = 0;
                }
                checkpoints.Should().Be(1, "the close checkpoint ran");
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "and wrote nothing either, without throwing");
                power.AfterPowerLoss(x => AssertRows(x, 10));

                power.LogFails = false;
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    engine.Checkpoint().Should().BeGreaterThan(0, "the log syncs again");
                    new FileInfo(logName).Length.Should().Be(0, "a checkpoint drains the WAL");
                    AssertRows(db, 30);
                }
                power.AfterPowerLoss(x => AssertRows(x, 30));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The log stops syncing exactly at a checkpoint's journal barrier, its first log sync (flipped
        /// at the checkpoint's first stage). Rows 11..20 were acknowledged durable, so they are in the
        /// log as of its last successful sync; rows 21..30 came after, opted out, in the OS cache only.
        /// The checkpoint refuses before its journal: exactly one rejected log sync, the data file byte
        /// for byte, the WAL kept, no failure recorded. A power loss keeps every row that reached the
        /// log's last successful sync or the data file, whole, and loses only 21..30; the data file is
        /// not torn, even where every write the OS holds for it reached the device.
        /// </summary>
        [Fact]
        public void Checkpoint_whose_log_stops_syncing_at_its_journal_barrier_writes_nothing_and_keeps_synced_rows()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            Setup(file.Filename, 10);
            using var power = new FilePowerLossModel(file.Filename);
            var hook = NativeFileSync.SimulateErrno;
            var rejected = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                var errno = hook(path);
                if (errno != 0 && string.Equals(Path.GetFullPath(path), Path.GetFullPath(logName), StringComparison.OrdinalIgnoreCase)) rejected++;
                return errno;
            };
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.CheckpointSize = 0; // no automatic or close checkpoint: 11..20 stay in the WAL
                    db.GetCollection("rows").Insert(Enumerable.Range(11, 10).Select(id => Row(id)));
                    Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                }
                var synced = power.Capture();

                var settings = new EngineSettings
                {
                    Filename = file.Filename, DurableCommits = false,
                    CheckpointStage = stage => { if (stage == "before-commit-lock") power.LogFails = true; }
                };
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    for (var id = 21; id <= 30; id++) db.GetCollection("rows").Insert(Row(id));
                    var data = SyncPowerLossModel.ReadShared(file.Filename);
                    var wal = SyncPowerLossModel.ReadShared(logName);
                    power.Capture().Log.Should().Equal(synced.Log, "opted-out commits do not sync the log");

                    engine.Checkpoint().Should().Be(0);
                    rejected.Should().Be(1, "the journal barrier is the checkpoint's first log sync, and the last");
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "nothing was written to the data file");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(wal, "the WAL is kept, without a journal footer");
                    power.Capture().Data.Should().Equal(data, "the data file synced before the barrier, as it was");
                    power.Capture().Log.Should().Equal(synced.Log);
                    Info(db)["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                    AssertRows(db, 30);

                    power.AfterPowerLoss(x => AssertRows(x, 20));
                    // Every write the OS holds for the data file reached the device, the log only as synced.
                    FilePowerLossModel.Open((SyncPowerLossModel.ReadShared(file.Filename), power.Capture().Log), x => AssertRows(x, 20));
                }

                power.LogFails = false;
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
                {
                    engine.Checkpoint().Should().BeGreaterThan(0);
                    new FileInfo(logName).Length.Should().Be(0, "once the log syncs, a checkpoint drains the WAL");
                }
                power.AfterPowerLoss(x => AssertRows(x, 30));
            }
            finally
            {
                NativeFileSync.SimulateErrno = hook;
                File.Delete(logName);
            }
        }

        /// <summary>
        /// While the log is known not to sync, no checkpoint can drain the WAL, so the WAL limit holds
        /// although the data file syncs (decision D). With a 64 KiB limit, every write that starts at or
        /// below it is accepted; past it an insert, an update and a new index throw a plain IOException
        /// before they change anything, and nothing is recorded: the limit is not a write failure. Reads
        /// keep working, and $database reports the WAL kept. Once the log syncs, the next write passes (it
        /// tries the log sync first, as for the data file), and a checkpoint drains the WAL.
        /// </summary>
        [Fact]
        public void Wal_limit_holds_while_the_log_cannot_sync_though_the_data_file_syncs()
        {
            const long limit = 64 * 1024;
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            Setup(file.Filename, 0);
            using var power = new FilePowerLossModel(file.Filename) { LogFails = true };
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, DurableCommits = false, WalLimit = limit });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                var rows = 0;
                db.GetCollection("rows").Insert(Row(++rows));
                var dataSyncs = power.DataSyncs;
                engine.Checkpoint().Should().Be(0, "the checkpoint finds out that the log cannot sync");
                power.DataSyncs.Should().BeGreaterThan(dataSyncs, "its data sync came first, and succeeded: the log is what holds the limit");
                while (Info(db)["logFileSize"].AsInt64 <= limit)
                {
                    rows.Should().BeLessThan(100, "the WAL grows with every commit");
                    db.GetCollection("rows").Insert(Row(++rows)); // started at or below the limit: accepted
                }
                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                AssertRefused(() => db.GetCollection("rows").Insert(Row(rows + 1)));
                AssertRefused(() => db.GetCollection("rows").Update(Row(1, value: 9)));
                AssertRefused(() => db.GetCollection("rows").EnsureIndex("payload"));
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1, "a refused write changes nothing");
                SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);

                AssertRows(db, rows);
                var info = Info(db);
                info["readOnly"].AsBoolean.Should().BeFalse("the limit is not a write failure");
                info["writeFailure"].IsNull.Should().BeTrue();
                info["walKept"].AsBoolean.Should().BeTrue("no checkpoint can drain the WAL while the log cannot sync");

                power.LogFails = false;
                // A write past the limit tries the log sync first: once it syncs, a checkpoint can drain the WAL.
                db.GetCollection("rows").Insert(Row(++rows));
                Info(db)["walKept"].AsBoolean.Should().BeFalse();
                engine.Checkpoint().Should().BeGreaterThan(0);
                new FileInfo(logName).Length.Should().Be(0, "once the log syncs, a checkpoint drains the WAL");
                power.AfterPowerLoss(x => AssertRows(x, rows));
                db.GetCollection("rows").Update(Row(1, value: 9));
                db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(9, "writes resume");
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Opted out, the first compact write to a v11 file (compact storage=auto) needs the v12 format,
        /// whose promotion overwrites the header behind a header journal: on a log that cannot sync it is
        /// refused before the journal, and the write falls back to BSON, as while the data file cannot
        /// sync. The write succeeds, the data file stays byte for byte and v11, and nothing is recorded
        /// (proposed default A). A refusal that reached the insert used to stop the engine for good.
        /// </summary>
        [Fact]
        public void Compact_promotion_on_a_log_that_cannot_sync_falls_back_to_bson_without_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();
            // A v11 file (the pattern of Issue2242_UnsyncableLog_Tests): legacy storage writes no compact document.
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(Enumerable.Range(1, 16).Select(id => Row(id)));
                db.Checkpoint();
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);
            var dataBefore = data.ToArray();

            var compact = Enumerable.Range(100, 16).Select(Engine.CompactStorage_Tests.Document).ToArray();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Auto, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("compact").Insert(compact);
                data.ToArray().Should().Equal(dataBefore, "the promotion was refused before it wrote");

                AssertRows(db, 16);
                db.GetCollection("compact").FindAll().Should().BeEquivalentTo(compact);
                Info(db)["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                Info(db)["walKept"].AsBoolean.Should().BeTrue("no checkpoint can drain the WAL while the log cannot sync");
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION, "the file stays v11: BSON only");
        }

        /// <summary>A database with the value index and rows 1..<paramref name="rows"/>, checkpointed and synced on close.</summary>
        private static void Setup(string filename, int rows)
        {
            using var db = new LiteDatabase(filename);
            db.GetCollection("rows").EnsureIndex("value");
            if (rows > 0) db.GetCollection("rows").Insert(Enumerable.Range(1, rows).Select(id => Row(id)));
        }

        private static BsonDocument Row(int id) => Row(id, id % 5);

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + id % 26), 1500) + id
        };

        /// <summary>"rows" holds exactly <see cref="Row(int)"/> 1..<paramref name="count"/>, byte for byte, and the value index finds each.</summary>
        private static int AssertRows(LiteDatabase db, int count)
        {
            var rows = db.GetCollection("rows");
            var all = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            all.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            for (var i = 0; i < count; i++) BsonSerializer.Serialize(all[i]).Should().Equal(BsonSerializer.Serialize(Row(i + 1)));
            for (var value = 0; value < 5; value++)
                rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(Enumerable.Range(1, count).Where(id => id % 5 == value));
            return count;
        }

        /// <summary>A write that started past the WAL limit threw a plain IOException before it changed anything.</summary>
        private static void AssertRefused(Action write) =>
            write.Should().Throw<IOException>().WithMessage("Cannot modify this database now: its log file (*) passed the WAL limit (64 KB) " +
                "while the log file cannot sync to the device (#2242)*")
                .Which.GetType().Should().Be(typeof(IOException));

        /// <summary>The WAL's bytes up to its last whole frame; the padding behind them is never a frame.</summary>
        private static byte[] Frames(byte[] log) => log.Take(log.Length / WalChecksum.FrameSize * WalChecksum.FrameSize).ToArray();

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        /// <summary>A log whose storage answers every device sync with ERROR_ACCESS_DENIED ("cannot sync").</summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            public void FlushToDisk() => throw new UnauthorizedAccessException("Access to the path is denied.");
        }
    }
}
#endif
